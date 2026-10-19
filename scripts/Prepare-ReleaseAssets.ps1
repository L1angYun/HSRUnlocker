# Stage reviewed local release files; never download, publish, or run an installer.
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$SourceArchivePath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseFileSystemPath([string]$Path) {
    [System.Management.Automation.ProviderInfo]$provider = $null
    [System.Management.Automation.PSDriveInfo]$drive = $null
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path, [ref]$provider, [ref]$drive)
    if ($provider.Name -cne 'FileSystem') { throw 'Release paths must use the FileSystem provider.' }
    return [IO.Path]::GetFullPath($resolved)
}

function Assert-NoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { $attributes = 0 }
        catch [IO.DirectoryNotFoundException] { $attributes = 0 }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Release paths must not contain reparse points.'
        }
        $parent = Split-Path -Parent $current
        if ([string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $parent
    }
}

function Assert-EmptyOutput {
    Assert-NoReparsePoint $OutDir
    if (Test-Path -LiteralPath $OutDir) {
        if (-not (Test-Path -LiteralPath $OutDir -PathType Container) -or
            @(Get-ChildItem -LiteralPath $OutDir -Force).Count -ne 0) {
            throw 'Release output must be a new or empty directory.'
        }
    }
}

function Assert-ReleaseProperties([Text.Json.JsonElement]$Value, [string[]]$Names) {
    if ($Value.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        throw 'Expected a release metadata object.'
    }
    $actual = @($Value.EnumerateObject() | ForEach-Object Name)
    if ($actual.Count -ne $Names.Count -or
        @(Compare-Object $Names $actual -CaseSensitive).Count -ne 0) {
        throw 'Unexpected, missing, or duplicate release metadata properties.'
    }
}

if ($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw 'Version must contain exactly three stable numeric components, for example 1.3.6.'
}
$InstallerPath = Get-ReleaseFileSystemPath $InstallerPath
$SourceArchivePath = Get-ReleaseFileSystemPath $SourceArchivePath
$OutDir = Get-ReleaseFileSystemPath $OutDir
$installerName = "HSRUnlocker-$Version-win-x64-Setup.exe"
if ([IO.Path]::GetFileName($InstallerPath) -cne $installerName) {
    throw "InstallerPath must identify $installerName."
}
$manifestPath = Join-Path ([IO.Path]::GetDirectoryName($InstallerPath)) 'update.json'
$inputs = @(
    [pscustomobject]@{ Name = $installerName; Path = $InstallerPath; Stream = $null; Size = 0L; SHA256 = $null },
    [pscustomobject]@{ Name = "HSRUnlocker-$Version-source.zip"; Path = $SourceArchivePath; Stream = $null; Size = 0L; SHA256 = $null },
    [pscustomobject]@{ Name = 'update.json'; Path = $manifestPath; Stream = $null; Size = 0L; SHA256 = $null }
)
Assert-EmptyOutput
$outputRoot = $OutDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($inputFile in $inputs) {
    Assert-NoReparsePoint $inputFile.Path
    if (-not (Test-Path -LiteralPath $inputFile.Path -PathType Leaf) -or
        (Get-Item -LiteralPath $inputFile.Path -Force).Length -le 0) {
        throw "Release input must be a nonempty file: $($inputFile.Path)"
    }
    $inputRoot = [IO.Path]::GetDirectoryName($inputFile.Path).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($outputRoot.StartsWith($inputRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $inputRoot.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release input and output directories must not overlap.'
    }
}

try {
    # Keep the selected files open for reading so their reviewed bytes cannot change during staging.
    foreach ($inputFile in $inputs) {
        $inputFile.Stream = [IO.File]::Open($inputFile.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $inputFile.Size = $inputFile.Stream.Length
        if ($inputFile.Size -le 0) { throw 'Release inputs must not be empty.' }
        $inputFile.SHA256 = (Get-FileHash -InputStream $inputFile.Stream -Algorithm SHA256).Hash.ToLowerInvariant()
        $inputFile.Stream.Position = 0
    }
    if ($inputs[2].Size -gt 65536) { throw 'Release metadata must not exceed 64 KiB.' }
    $manifestBuffer = [IO.MemoryStream]::new([int]$inputs[2].Size)
    try {
        $inputs[2].Stream.CopyTo($manifestBuffer)
        $manifestBytes = $manifestBuffer.ToArray()
    }
    finally { $manifestBuffer.Dispose() }
    $jsonOptions = [Text.Json.JsonDocumentOptions]::new()
    $jsonOptions.MaxDepth = 8
    $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($manifestBytes), $jsonOptions)
    try {
        $manifest = $document.RootElement
        Assert-ReleaseProperties $manifest @('tag_name', 'draft', 'prerelease', 'assets')
        $tag = $manifest.GetProperty('tag_name')
        if ($tag.ValueKind -ne [Text.Json.JsonValueKind]::String -or $tag.GetString() -cne "v$Version" -or
            $manifest.GetProperty('draft').ValueKind -ne [Text.Json.JsonValueKind]::False -or
            $manifest.GetProperty('prerelease').ValueKind -ne [Text.Json.JsonValueKind]::False) {
            throw 'Release metadata must identify the requested stable, published version.'
        }
        $assets = $manifest.GetProperty('assets')
        if ($assets.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $assets.GetArrayLength() -ne 1) {
            throw 'Release metadata must contain exactly one installer entry.'
        }
        $asset = $assets[0]
        Assert-ReleaseProperties $asset @('name', 'size', 'digest')
        $name = $asset.GetProperty('name')
        $size = $asset.GetProperty('size')
        $digest = $asset.GetProperty('digest')
        [long]$declaredSize = 0
        if ($name.ValueKind -ne [Text.Json.JsonValueKind]::String -or $name.GetString() -cne $installerName -or
            $size.ValueKind -ne [Text.Json.JsonValueKind]::Number -or -not $size.TryGetInt64([ref]$declaredSize) -or
            $declaredSize -ne $inputs[0].Size -or
            $digest.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $digest.GetString() -cne ('sha256:' + $inputs[0].SHA256)) {
            throw 'Release metadata installer name, size, and SHA-256 must match the selected file.'
        }
    }
    finally { $document.Dispose() }

    $archive = [IO.Compression.ZipArchive]::new($inputs[1].Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $hasSourceData = $false
        $buffer = [byte[]]::new(65536)
        foreach ($entry in $archive.Entries) {
            if ($entry.Name.Length -eq 0) { continue }
            [long]$readLength = 0
            $entryStream = $entry.Open()
            try {
                while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $readLength += $read
                    if ($readLength -gt $entry.Length) { throw 'ZIP entry data exceeds its declared length.' }
                }
            }
            finally { $entryStream.Dispose() }
            if ($readLength -ne $entry.Length) { throw 'ZIP entry data does not match its declared length.' }
            if ($readLength -gt 0) { $hasSourceData = $true }
        }
        if (-not $hasSourceData) { throw 'Source archive must be a readable ZIP containing nonempty file data.' }
    }
    finally { $archive.Dispose() }

    # Validate all inputs before creating output, and refuse each destination if it already exists.
    Assert-EmptyOutput
    [IO.Directory]::CreateDirectory($OutDir) | Out-Null
    $files = [Collections.Generic.List[object]]::new()
    $checksumLines = [Collections.Generic.List[string]]::new()
    foreach ($inputFile in $inputs) {
        $destination = Join-Path $OutDir $inputFile.Name
        Assert-NoReparsePoint $destination
        $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $inputFile.Stream.Position = 0
            $inputFile.Stream.CopyTo($outputStream)
        }
        finally { $outputStream.Dispose() }
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $destination -Force).Length
        if ($hash -cne $inputFile.SHA256 -or $size -ne $inputFile.Size) {
            throw "Staged file does not match its reviewed input: $($inputFile.Name)"
        }
        $files.Add([pscustomobject]@{ Name = $inputFile.Name; Path = $destination; Size = $size; SHA256 = $hash })
        $checksumLines.Add("$hash  $($inputFile.Name)")
    }
    $checksumsPath = Join-Path $OutDir 'SHA256SUMS.txt'
    Assert-NoReparsePoint $checksumsPath
    $checksumBytes = [Text.Encoding]::UTF8.GetBytes(($checksumLines -join "`n") + "`n")
    $outputStream = [IO.File]::Open($checksumsPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $outputStream.Write($checksumBytes, 0, $checksumBytes.Length) }
    finally { $outputStream.Dispose() }
    $files.Add([pscustomobject]@{
        Name = 'SHA256SUMS.txt'; Path = $checksumsPath; Size = (Get-Item -LiteralPath $checksumsPath -Force).Length
        SHA256 = (Get-FileHash -LiteralPath $checksumsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    Assert-NoReparsePoint $OutDir
    $staged = @(Get-ChildItem -LiteralPath $OutDir -Force)
    if ($staged.Count -ne 4 -or @($staged | Where-Object {
        $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $_.Name -cnotin $files.Name
    }).Count -ne 0) { throw 'Release output contains unexpected files.' }
    [pscustomobject]@{ Version = $Version; OutputDirectory = $OutDir; Files = $files.ToArray() }
}
finally {
    foreach ($inputFile in $inputs) {
        if ($null -ne $inputFile.Stream) { $inputFile.Stream.Dispose() }
    }
}

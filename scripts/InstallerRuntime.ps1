function Read-InstallerJson([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -gt 65536) { throw 'Installer metadata must be a bounded JSON file.' }
    return [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
}

function Assert-InstallerProperties($Value, [string[]]$Names) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'Expected a metadata object.' }
    $actual = @($Value.PSObject.Properties.Name)
    if (@(Compare-Object $Names $actual -CaseSensitive).Count -ne 0) { throw 'Unexpected installer metadata properties.' }
}

function Read-InstallerRuntimeManifest {
    param([string]$Path, [string]$ExpectedSdkVersion)
    $manifest = Read-InstallerJson $Path
    Assert-InstallerProperties $manifest @('schemaVersion', 'dotnet', 'windowsAppRuntime')
    if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or
        $manifest.schemaVersion -ne 1) { throw 'Unsupported runtime metadata schema.' }
    foreach ($name in @('dotnet', 'windowsAppRuntime')) {
        $entry = $manifest.$name
        $properties = @('version', 'architecture', 'url', 'sha256', 'bytes')
        if ($name -eq 'dotnet') { $properties += 'minimumVersion' }
        Assert-InstallerProperties $entry $properties
        if ($entry.architecture -cne 'x64' -or $entry.sha256 -isnot [string] -or
            $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $entry.version -isnot [string] -or
            $entry.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
            ($entry.bytes -isnot [int] -and $entry.bytes -isnot [long]) -or
            $entry.bytes -le 0 -or $entry.bytes -gt 536870912) { throw 'Invalid runtime identity, hash, or size.' }
        $version = [version]$entry.version
        if ($version.Major -gt 32767 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) {
            throw 'Runtime version exceeds the native version representation.'
        }
        [Uri]$uri = $null
        if ($entry.url -isnot [string] -or -not [Uri]::TryCreate($entry.url, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cne 'https' -or $uri.Port -ne 443 -or $uri.UserInfo.Length -ne 0 -or
            $uri.Query.Length -ne 0 -or $uri.Fragment.Length -ne 0 -or $uri.AbsoluteUri -cne $entry.url) {
            throw 'Runtime downloads require canonical HTTPS URLs without credentials or parameters.'
        }
        if ($name -eq 'dotnet') {
            if ($version.Major -ne 8 -or $version.Minor -ne 0 -or
                $entry.minimumVersion -cne '8.0.0' -or [version]$entry.minimumVersion -gt $version -or
                $uri.Host -cne 'builds.dotnet.microsoft.com' -or
                $uri.AbsolutePath -cne "/dotnet/Runtime/$version/dotnet-runtime-$version-win-x64.exe") {
                throw 'The .NET runtime does not match this application or the official installer path.'
            }
        }
        elseif ($entry.version -cne $ExpectedSdkVersion -or $uri.Host -cne 'download.microsoft.com' -or
            $uri.AbsolutePath -cnotmatch '^/download/[0-9a-f-]{36}/WindowsAppRuntimeInstall-x64\.exe$') {
            throw 'The Windows App Runtime does not match the SDK or the official installer path.'
        }
    }
    return $manifest
}

function Assert-FrameworkDependentPayload {
    param([string]$PublishDir)
    $runtime = Read-InstallerJson (Join-Path $PublishDir 'HSRUnlocker.runtimeconfig.json')
    $framework = $runtime.runtimeOptions.framework
    if ($framework.name -cne 'Microsoft.NETCore.App' -or $framework.version -cne '8.0.0' -or
        $null -ne $runtime.runtimeOptions.frameworks -or $null -ne $runtime.runtimeOptions.includedFrameworks) {
        throw 'The installer must ship a framework-dependent .NET 8 payload.'
    }
    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll')) {
        if (Test-Path -LiteralPath (Join-Path $PublishDir $name)) { throw "Bundled runtime file in application payload: $name" }
    }
    $bootstrap = Get-Item -LiteralPath (Join-Path $PublishDir 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
    if ($bootstrap.PSIsContainer -or $bootstrap.Length -eq 0) { throw 'The app bootstrap DLL is missing.' }
    $stream = [IO.File]::OpenRead($bootstrap.FullName)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 128 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid native bootstrap DLL.' }
        $stream.Position = 0x3C
        $headerOffset = $reader.ReadUInt32()
        if ($headerOffset -gt ($stream.Length - 26)) { throw 'Invalid native bootstrap PE header.' }
        $stream.Position = $headerOffset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'The native bootstrap DLL must be x64.'
        }
        $stream.Position = $headerOffset + 24
        if ($reader.ReadUInt16() -ne 0x20B) { throw 'The native bootstrap DLL must use PE32+.' }
    }
    finally { $reader.Dispose() }
}

function Get-LegacyRuntimeFiles {
    param([string]$Path, [string]$PublishDir)
    $manifest = Read-InstallerJson $Path
    Assert-InstallerProperties $manifest @('schemaVersion', 'files')
    if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or
        $manifest.schemaVersion -ne 1 -or $manifest.files -isnot [Array] -or
        $manifest.files.Count -eq 0 -or $manifest.files.Count -gt 4096) { throw 'Invalid legacy runtime inventory.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $manifest.files) {
        if ($relative -isnot [string] -or $relative.Length -gt 240 -or
            $relative -cnotmatch '^[A-Za-z0-9_. -]+(/[A-Za-z0-9_. -]+)*$' -or
            -not $seen.Add($relative)) { throw 'Unsafe or duplicate legacy runtime path.' }
        foreach ($part in $relative.Split('/')) {
            if ($part -in @('.', '..') -or $part -ne $part.Trim(' ', '.') -or
                $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw 'Invalid Windows path in legacy inventory.' }
        }
        $leaf = [IO.Path]::GetFileName($relative)
        if ($relative.StartsWith('Assets/', [StringComparison]::OrdinalIgnoreCase) -or
            $leaf -match '^HSRUnlocker([.]|$)' -or
            $leaf -in @('settings.json', 'hsr_config.json', 'config.json', 'App.xbf', 'MainWindow.xbf') -or
            $relative -in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) { throw 'Protected file in legacy runtime inventory.' }
        if (-not [string]::IsNullOrWhiteSpace($PublishDir) -and
            (Test-Path -LiteralPath (Join-Path $PublishDir $relative))) { throw 'A legacy cleanup entry belongs to the current payload.' }
        $relative
    }
}

function Write-LegacyRuntimeDeleteScript {
    param([string]$ManifestPath, [string]$PublishDir, [string]$Destination)
    $files = @(Get-LegacyRuntimeFiles $ManifestPath $PublishDir)
    $lines = @('[InstallDelete]')
    $lines += foreach ($relative in $files) {
        'Type: files; Name: "{{app}}\{0}"; Check: CanDeleteLegacyRuntimeFile(''{1}'')' -f $relative.Replace('/', '\'), $relative
    }
    [IO.File]::WriteAllLines($Destination, [string[]]$lines, [Text.UTF8Encoding]::new($false))
}

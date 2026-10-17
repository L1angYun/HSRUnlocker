# Copy dependency terms verbatim; this inventory is not a licensing determination.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

function Assert-NoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing to traverse a reparse point while collecting dependency notices.'
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $parent
    }
}

function Get-SafeChildPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) {
        throw 'Dependency paths must be nonempty and relative.'
    }
    $parts = $Relative -split '[/\\]'
    foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -in @('.', '..') -or
            $part -match '[<>:"|?*\x00-\x1f]' -or $part.EndsWith('.') -or $part.EndsWith(' ')) {
            throw 'Unsafe dependency path component.'
        }
    }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $base ($parts -join [IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Dependency path escapes its intended directory.'
    }
    Assert-NoReparsePoint $path
    return $path
}

function Add-Package([string]$Id, [string]$Version, [string]$Origin, [string]$Relative) {
    if ($Id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $Id.EndsWith('.') -or
        $Version -notmatch '^[A-Za-z0-9][A-Za-z0-9.+-]*$' -or $Version.EndsWith('.')) {
        throw 'Invalid resolved NuGet package identity.'
    }
    $expected = $Id.ToLowerInvariant() + '/' + $Version.ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($Relative) -and
        -not [string]::Equals($Relative.Replace('\', '/'), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Resolved package path does not match its package identity.'
    }
    if ($packages.Contains($expected)) {
        if ($packages[$expected].origins -notcontains $Origin) { $packages[$expected].origins += $Origin }
    }
    else {
        $packages[$expected] = [pscustomobject]@{
            id = $Id; version = $Version; relative = $expected; origins = @($Origin)
        }
    }
}

if (-not (Test-Path -LiteralPath $AssetsFile -PathType Leaf)) { throw 'NuGet assets file is missing.' }
Assert-NoReparsePoint $AssetsFile
$assets = Get-Content -LiteralPath $AssetsFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($assets.version -ne 3) { throw 'Unsupported NuGet assets format.' }
$roots = @($assets.packageFolders.PSObject.Properties.Name)
if ($roots.Count -eq 0) { throw 'NuGet assets do not identify a package cache.' }
foreach ($root in $roots) {
    if (-not [IO.Path]::IsPathRooted($root)) { throw 'NuGet package cache must be an absolute path.' }
}

$packages = [ordered]@{}
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $identity = $library.Name -split '/'
    if ($identity.Count -ne 2) { throw 'Invalid resolved NuGet library identity.' }
    if ([string]::IsNullOrWhiteSpace($library.Value.path)) { throw 'Resolved package path is missing.' }
    Add-Package $identity[0] $identity[1] 'library' $library.Value.path
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($download in $framework.Value.downloadDependencies) {
        $range = [string]$download.version
        $version = $null
        if ($range -match '^\[\s*([^,\[\]\s]+)\s*,\s*([^,\[\]\s]+)\s*\]$') {
            if (-not [string]::Equals($Matches[1], $Matches[2], [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Download dependency version is not exact.'
            }
            $version = $Matches[1]
        }
        elseif ($range -match '^\[\s*([^,\[\]\s]+)\s*\]$') { $version = $Matches[1] }
        else { throw 'Download dependency version is not an exact range.' }
        Add-Package $download.name $version 'downloadDependency' ''
    }
}
if ($packages.Count -eq 0) { throw 'NuGet assets contain no dependency packages.' }

$OutDir = [IO.Path]::GetFullPath($OutDir)
Assert-NoReparsePoint $OutDir
if (Test-Path -LiteralPath $OutDir) {
    if (-not (Test-Path -LiteralPath $OutDir -PathType Container) -or
        @(Get-ChildItem -LiteralPath $OutDir -Force).Count -ne 0) {
        throw 'Dependency notice output must be an empty directory.'
    }
}

$inventory = New-Object 'System.Collections.Generic.List[object]'
$copies = New-Object 'System.Collections.Generic.List[object]'
$textExtensions = @('', '.txt', '.md', '.markdown', '.rst', '.rtf', '.htm', '.html', '.xml')
foreach ($package in $packages.Values) {
    $packagePath = $null
    foreach ($root in $roots) {
        $candidate = Get-SafeChildPath $root $package.relative
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
    }
    if ($null -eq $packagePath) { throw "Resolved dependency package is missing: $($package.id)/$($package.version)." }
    $nuspecPath = Get-SafeChildPath $packagePath ($package.id.ToLowerInvariant() + '.nuspec')
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
        throw "Dependency metadata is missing: $($package.id)/$($package.version)."
    }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($nuspecPath, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($null -eq $metadata) { throw 'Dependency metadata has no package element.' }
    $idNode = $metadata.SelectSingleNode('*[local-name()="id"]')
    $versionNode = $metadata.SelectSingleNode('*[local-name()="version"]')
    if ($null -eq $idNode -or $null -eq $versionNode -or
        -not [string]::Equals($idNode.InnerText, $package.id, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($versionNode.InnerText, $package.version, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Dependency metadata does not match the resolved package identity.'
    }
    $license = [ordered]@{ source = 'NuGet nuspec'; type = 'unspecified'; value = $null; url = $null }
    $declaredFile = $null
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    if ($null -ne $licenseNode) {
        $license.type = $licenseNode.GetAttribute('type')
        $license.value = $licenseNode.InnerText.Trim()
        if ($license.type -eq 'file') {
            $declaredFile = Get-SafeChildPath $packagePath $license.value
            if (-not (Test-Path -LiteralPath $declaredFile -PathType Leaf)) {
                throw 'A dependency declares a license file that is missing.'
            }
            if ($textExtensions -notcontains [IO.Path]::GetExtension($declaredFile).ToLowerInvariant()) {
                throw 'A dependency license file is not a supported text document.'
            }
            $license.value = $license.value.Replace('\', '/')
        }
        elseif ($license.type -eq 'expression') {
            if ($license.value -notmatch '^[A-Za-z0-9.+() -]+$') { throw 'Invalid dependency license expression.' }
        }
        else { throw 'Unsupported dependency license metadata type.' }
    }
    $urlNode = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    if ($null -ne $urlNode -and -not [string]::IsNullOrWhiteSpace($urlNode.InnerText)) {
        $uri = $null
        $url = $urlNode.InnerText.Trim()
        if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -notin @('https', 'http') -or -not [string]::IsNullOrEmpty($uri.UserInfo) -or
            -not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
            throw 'Dependency license URL is not a plain public HTTP(S) URL.'
        }
        $license.url = $url
    }

    $files = [ordered]@{}
    if ($null -ne $declaredFile) { $files[$declaredFile] = $true }
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($packagePath)
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Dependency package contains a reparse point.'
            }
            if ($item.PSIsContainer) { $directories.Push($item.FullName); continue }
            if ($textExtensions -contains $item.Extension.ToLowerInvariant() -and
                $item.BaseName -match '(?i)(^|[-_. ])(licen[sc]e|notice|third[-_. ]?party[-_. ]?notice|copying|copyright)') {
                $files[$item.FullName] = $true
            }
        }
    }
    $copiedFiles = @()
    foreach ($source in @($files.Keys | Sort-Object)) {
        $relative = $source.Substring($packagePath.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        $null = Get-SafeChildPath $packagePath $relative
        $destinationRelative = $package.relative + '/' + $relative
        $destination = Get-SafeChildPath $OutDir $destinationRelative
        $copies.Add([pscustomobject]@{ source = $source; destination = $destination })
        $copiedFiles += $destinationRelative
    }
    $inventory.Add([pscustomobject]@{
        id = $idNode.InnerText
        version = $versionNode.InnerText
        origins = @($package.origins)
        license = $license
        copiedFiles = @($copiedFiles)
        embeddedTextMissing = $copiedFiles.Count -eq 0
    })
}

# Validate every package before writing any files or a success-looking manifest.
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
foreach ($copy in $copies) {
    Assert-NoReparsePoint $copy.source
    Assert-NoReparsePoint $copy.destination
    New-Item -ItemType Directory -Path (Split-Path -Parent $copy.destination) -Force | Out-Null
    Copy-Item -LiteralPath $copy.source -Destination $copy.destination
}
$manifest = [ordered]@{
    schemaVersion = 1
    scope = [ordered]@{
        source = 'NuGet assets libraries and framework downloadDependencies'
        selfContainedRequested = [bool]$SelfContained
        manualReview = @(
            'Only packages recorded in these NuGet assets are inventoried; review the actual published components separately.'
            'SDK, operating-system components, and runtime packs absent from these assets require separate review, including self-contained deployments.'
            'URL-only or expression-only metadata does not supply an embedded license text. This collection is not a license-compatibility or redistribution clearance.'
        )
    }
    packages = $inventory.ToArray()
}
[IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10),
    (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{
    Packages = $inventory.Count
    CopiedFiles = $copies.Count
    PackagesWithoutEmbeddedText = @($inventory | Where-Object { $_.embeddedTextMissing }).Count
}

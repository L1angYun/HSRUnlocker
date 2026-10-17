# Package approved source, font assets and licenses, tests, build scripts, and documentation.
#
# Exclude build output, NuGet caches, game files, dumps, logs, local settings,
# original executables, and decompilation output.
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'artifacts' }

$include = @(
    'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md',
    'global.json', 'build.ps1', 'build-installer.ps1', 'installer\HSRUnlocker.iss',
    'package-source.ps1', '.gitignore', '.gitattributes', '.editorconfig',
    'docs\VALIDATION.md', '.github\workflows\build.yml',
    'scripts\Copy-ThirdPartyNotices.ps1', 'tests\Packaging.Tests.ps1',
    'gui\App.xaml', 'gui\App.xaml.cs', 'gui\MainWindow.xaml', 'gui\MainWindow.xaml.cs',
    'gui\MainWindow.Presentation.cs', 'gui\MainWindow.Localization.cs', 'gui\MainWindow.Updates.cs',
    'gui\WindowLayout.cs', 'gui\Assets\AppIcon.png', 'gui\Assets\AppIcon.ico',
    'gui\Assets\Fonts\MiSansVF.ttf', 'gui\Assets\Fonts\MiSans-LICENSE.pdf', 'gui\Assets\Fonts\NOTICE.md',
    'gui\TrayIcon.cs', 'gui\HSRUnlocker.csproj', 'gui\app.manifest'
)
$include += (Get-ChildItem -LiteralPath (Join-Path $repo 'gui\Core') -Filter '*.cs' -File |
    ForEach-Object { 'gui\Core\' + $_.Name })
$include += (Get-ChildItem -LiteralPath (Join-Path $repo 'gui\Localization') -Filter '*.json' -File |
    ForEach-Object { 'gui\Localization\' + $_.Name })
$include += (Get-ChildItem -LiteralPath (Join-Path $repo 'tests\HSRUnlocker.Tests') -Filter '*.cs' -File |
    ForEach-Object { 'tests\HSRUnlocker.Tests\' + $_.Name })
$include += 'tests\HSRUnlocker.Tests\HSRUnlocker.Tests.csproj'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $OutDir).Path)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stagingName = "HSRUnlocker-source-$stamp"
$staging = Join-Path $OutDir $stagingName
if (Test-Path -LiteralPath $staging) { throw "Staging directory already exists: $staging" }
New-Item -ItemType Directory -Path $staging | Out-Null

$copied = 0
foreach ($relative in $include) {
    $source = Join-Path $repo $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing required source file: $relative" }
    $destination = Join-Path $staging $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    $copied++
}

$forbidden = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object {
    $_.Extension -in @('.dll', '.exe', '.dmp', '.zip', '.7z', '.nupkg', '.log', '.pfx', '.snk') -or
    $_.Name -in @('settings.json', 'hsr_config.json')
})
if ($forbidden.Count -gt 0) { throw "Refusing to ship forbidden files: $($forbidden.Name -join ', ')" }

$archive = Join-Path $OutDir ("HSRUnlocker-source-$stamp.zip")
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$stagingItem = Get-Item -LiteralPath $staging -Force
$resolvedStaging = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $staging).Path)
$resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedStaging))
if (-not $stagingItem.PSIsContainer -or
    ($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    -not [string]::Equals($resolvedParent, $OutDir, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([IO.Path]::GetFileName($resolvedStaging), $stagingName, [StringComparison]::Ordinal)) {
    throw "Refusing to remove an unexpected staging directory: $resolvedStaging"
}
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force

$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries)
}
finally { $zip.Dispose() }
if ($entries.Count -ne $copied) { throw "Archive entry count $($entries.Count) does not match the $copied approved files." }
[pscustomobject]@{
    Archive = $archive
    Bytes = (Get-Item -LiteralPath $archive).Length
    Entries = $entries.Count
    Sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
} | ConvertTo-Json

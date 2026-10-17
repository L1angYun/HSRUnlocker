# Publish a fresh self-contained app and compile an installer with a local Inno Setup.
# This script never downloads tools or launches the app, game, or generated installer.
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$OutDir,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'gui\HSRUnlocker.csproj'
$definition = Join-Path $repo 'installer\HSRUnlocker.iss'

function Assert-NoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing to use a reparse point in an installer build path.'
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $parent
    }
}

function Assert-EmptyDirectory([string]$Path, [string]$Name) {
    Assert-NoReparsePoint $Path
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            throw "$Name must be a directory: $Path"
        }
        if (@(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0) {
            throw "$Name is not empty: $Path. Choose a new empty directory."
        }
    }
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) { $PublishDir = Join-Path $repo 'artifacts\installer-publish' }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'artifacts\installer' }
$PublishDir = [IO.Path]::GetFullPath($PublishDir)
$OutDir = [IO.Path]::GetFullPath($OutDir)
Assert-EmptyDirectory $OutDir 'Output directory'
Assert-EmptyDirectory $PublishDir 'Publish directory'
$publishRoot = $PublishDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputRoot = $OutDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($publishRoot.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish and installer output directories must not overlap.'
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $CompilerPath = $command.Source }
    if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
        foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
            if ([string]::IsNullOrWhiteSpace($root)) { continue }
            $candidate = Join-Path $root 'Inno Setup 7\ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CompilerPath = $candidate; break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Compiler was not found. Install Inno Setup 7.1 or newer, or pass -CompilerPath to its ISCC.exe.'
}
$CompilerPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $CompilerPath).Path)
if ([IO.Path]::GetFileName($CompilerPath) -ine 'ISCC.exe') {
    throw 'CompilerPath must identify the Inno Setup ISCC.exe compiler.'
}
$compilerInfo = (Get-Item -LiteralPath $CompilerPath).VersionInfo
if ($compilerInfo.FileDescription -notmatch 'Inno Setup') {
    throw 'CompilerPath must identify the Inno Setup ISCC.exe compiler.'
}
$compilerVersion = (& $CompilerPath '--version' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [version]$compilerVersion -lt [version]'7.1.0') {
    throw 'The compiler must be Inno Setup 7.1 or newer.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf) -or
    -not (Test-Path -LiteralPath $definition -PathType Leaf)) {
    throw 'The application project or installer definition is missing.'
}

$settings = New-Object Xml.XmlReaderSettings
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($project, $settings)
try {
    $document = New-Object Xml.XmlDocument
    $document.XmlResolver = $null
    $document.Load($reader)
}
finally { $reader.Dispose() }
$versionNodes = @($document.SelectNodes('/Project/PropertyGroup/Version'))
$fileVersionNodes = @($document.SelectNodes('/Project/PropertyGroup/FileVersion'))
if ($versionNodes.Count -ne 1 -or $fileVersionNodes.Count -ne 1) {
    throw 'The project must declare exactly one Version and FileVersion.'
}
$appVersion = $versionNodes[0].InnerText.Trim()
$fileVersion = $fileVersionNodes[0].InnerText.Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    [version]$fileVersion -ne [version]($appVersion + '.0')) {
    throw 'The installer requires a stable numeric project Version and matching FileVersion.'
}
$outputName = "HSRUnlocker-$appVersion-win-x64-Setup"

& (Join-Path $repo 'build.ps1') -Project $project -OutDir $PublishDir -SelfContained

foreach ($binary in @('HSRUnlocker.exe', 'HSRUnlocker.dll')) {
    $info = (Get-Item -LiteralPath (Join-Path $PublishDir $binary)).VersionInfo
    if ([version]$info.FileVersion -ne [version]$fileVersion) {
        throw "Published $binary version does not match the project FileVersion."
    }
}
$productVersion = (Get-Item -LiteralPath (Join-Path $PublishDir 'HSRUnlocker.dll')).VersionInfo.ProductVersion
if (($productVersion -split '\+', 2)[0] -cne $appVersion) {
    throw 'Published application version does not match the project Version.'
}
foreach ($item in Get-ChildItem -LiteralPath $PublishDir -Recurse -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Published payload contains a reparse point.'
    }
    if ($item.PSIsContainer) {
        if ($item.Name -in @('.git', '.codex', 'obj', 'bin', 'node_modules')) {
            throw 'Published payload contains a source or cache directory.'
        }
        continue
    }
    if ($item.Extension -in @('.pdb', '.cs', '.csproj', '.sln', '.ps1', '.iss', '.nupkg', '.user', '.suo')) {
        throw "Published payload contains a source, debug, or private build file: $($item.Name)"
    }
}

# Recheck after publishing so a stale or concurrently populated output cannot be mixed in.
Assert-EmptyDirectory $OutDir 'Output directory'
$arguments = @('--quiet-progress', '--no-ide-signtools', "--define=AppVersion=$appVersion",
               "--define=AppFileVersion=$fileVersion", "--define=PublishDir=$PublishDir",
               "--output-dir=$OutDir", "--output-filename=$outputName", $definition)
& $CompilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
$installer = Join-Path $OutDir ($outputName + '.exe')
if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or
    (Get-Item -LiteralPath $installer).Length -le 0) {
    throw 'Inno Setup did not produce the expected installer.'
}
$installerInfo = (Get-Item -LiteralPath $installer).VersionInfo
if ([version]$installerInfo.FileVersion -ne [version]$fileVersion) {
    throw 'The generated installer version does not match the application.'
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$updateManifest = [ordered]@{
    tag_name = "v$appVersion"
    draft = $false
    prerelease = $false
    assets = @([ordered]@{
        name = [IO.Path]::GetFileName($installer)
        size = (Get-Item -LiteralPath $installer).Length
        digest = 'sha256:' + $hash
    })
}
$manifestPath = Join-Path $OutDir 'update.json'
[IO.File]::WriteAllText($manifestPath, ($updateManifest | ConvertTo-Json -Depth 4),
    (New-Object Text.UTF8Encoding($false)))
Write-Host "OK: installer $installer" -ForegroundColor Green
Write-Host "SHA256: $hash"
[pscustomobject]@{ Installer = $installer; UpdateManifest = $manifestPath; PublishDirectory = $PublishDir; Version = $appVersion; SHA256 = $hash }

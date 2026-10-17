# Build and publish the WinUI 3 app, then verify compiled XAML resources.
#
# Only run dotnet publish and copy files locally; do not launch the app or game,
# write registry keys, or access game directories. Require an empty output directory.
[CmdletBinding()]
param(
    [string]$Project,
    [string]$OutDir,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Project)) { $Project = Join-Path $repo 'gui\HSRUnlocker.csproj' }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo 'artifacts\win-x64' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet was not found on PATH.' }
if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Project file not found: $Project" }

$projectPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Project).Path)
$gui = Split-Path -Parent $projectPath
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$configuration = 'Release'
$tfm = 'net8.0-windows10.0.22621.0'
$rid = 'win-x64'
$bin = Join-Path $gui "bin\$configuration\$tfm\$rid"
$obj = Join-Path $gui "obj\$configuration\$tfm\$rid"
$resources = @('App.xbf', 'MainWindow.xbf', 'HSRUnlocker.pri')

if (Test-Path -LiteralPath $OutDir) {
    if ((Get-ChildItem -LiteralPath $OutDir -Force | Measure-Object).Count -gt 0) {
        throw "Output directory is not empty: $OutDir. Choose a new empty directory so stale files cannot be shipped."
    }
}

Write-Host "--- publishing HSRUnlocker to $OutDir ---" -ForegroundColor Green
$arguments = @('publish', $projectPath, '-c', $configuration, '-r', $rid, '-o', $OutDir)
$arguments += @('-p:PublishSingleFile=false', '-p:PublishTrimmed=false')
if ($SelfContained) {
    $arguments += @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true')
}
else {
    $arguments += @('--no-self-contained', '-p:WindowsAppSDKSelfContained=false')
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host '--- verifying compiled XAML resources ---' -ForegroundColor Green
foreach ($resource in $resources) {
    $destination = Join-Path $OutDir $resource
    $candidates = @((Join-Path $bin $resource), (Join-Path $obj $resource))
    $source = $null
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and ((Get-Item -LiteralPath $candidate).Length -gt 0)) {
            $source = $candidate
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($source)) {
        throw "Required compiled resource is missing from publish output and Release output: $resource"
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    if ((Get-Item -LiteralPath $destination).Length -le 0) { throw "Failed to copy required compiled resource: $resource" }
    Write-Host "  copied:  $resource" -ForegroundColor Cyan
}

# Include licence and third-party notices with redistributed binaries.
foreach ($document in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repo $document) -Destination $OutDir -Force
}

Write-Host '--- collecting dependency licence texts and notices ---' -ForegroundColor Green
$notices = & (Join-Path $repo 'scripts\Copy-ThirdPartyNotices.ps1') `
    -AssetsFile (Join-Path $gui 'obj\project.assets.json') `
    -OutDir (Join-Path $OutDir 'ThirdPartyNotices') -SelfContained:$SelfContained
Write-Host ("  packages: {0}; copied texts: {1}; packages without embedded texts: {2}" -f
    $notices.Packages, $notices.CopiedFiles, $notices.PackagesWithoutEmbeddedText) -ForegroundColor Cyan

$required = @('HSRUnlocker.exe', 'HSRUnlocker.dll', 'HSRUnlocker.deps.json', 'HSRUnlocker.runtimeconfig.json',
              'LICENSE', 'THIRD_PARTY_NOTICES.md', 'ThirdPartyNotices\manifest.json',
              'Assets\Fonts\MiSansVF.ttf', 'Assets\Fonts\MiSans-LICENSE.pdf', 'Assets\Fonts\NOTICE.md') + $resources
if ($SelfContained) {
    $required += @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
                   'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.UI.Xaml.Controls.pri',
                   'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
                   'Microsoft.Windows.ApplicationModel.Resources.dll', 'MRM.dll',
                   'Microsoft.UI.dll', 'Microsoft.UI.pri', 'Microsoft.UI.Windowing.dll', 'Microsoft.UI.Input.dll')
}
$missing = @($required | Where-Object {
    $path = Join-Path $OutDir $_
    -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0
})
if ($missing.Count -gt 0) { throw "Deliverable incomplete; missing: $($missing -join ', ')" }
if ($SelfContained) {
    $runtime = Get-Content -LiteralPath (Join-Path $OutDir 'HSRUnlocker.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($null -ne $runtime.runtimeOptions.framework -or $null -ne $runtime.runtimeOptions.frameworks -or
        @($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -ne 1) {
        throw 'Self-contained publish still requires an installed .NET runtime.'
    }
}

Write-Host "OK: published to $OutDir" -ForegroundColor Green
Write-Host 'Binary redistribution of Microsoft runtime components needs a separate licence review; see THIRD_PARTY_NOTICES.md.' -ForegroundColor Yellow

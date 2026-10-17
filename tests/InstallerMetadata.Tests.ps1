[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts\InstallerRuntime.ps1')
$passed = 0
$fixtureName = 'HSRUnlocker-installer-metadata-' + [Guid]::NewGuid().ToString('N')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $fixtureName))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$manifestPath = Join-Path $fixtureRoot 'dependencies.json'
$payload = Join-Path $fixtureRoot 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
$legacyPath = Join-Path $fixtureRoot 'legacy.json'
$baseline = [IO.File]::ReadAllText((Join-Path $repo 'installer\runtime-dependencies.json'))

function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "FAIL $Name" }
    $script:passed++
    Write-Host "PASS $Name"
}

function Write-Json($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Reject-Manifest([string]$Name, [scriptblock]$Change) {
    $value = $baseline | ConvertFrom-Json
    & $Change $value
    Write-Json $value $manifestPath
    $rejected = $false
    try { Read-InstallerRuntimeManifest $manifestPath '2.5.1' | Out-Null }
    catch { $rejected = $true }
    Check $rejected $Name
}

function Set-Payload([bool]$SelfContained) {
    $options = if ($SelfContained) {
        @{ includedFrameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '8.0.32' }) }
    } else { @{ framework = @{ name = 'Microsoft.NETCore.App'; version = '8.0.0' } } }
    Write-Json @{ runtimeOptions = $options } (Join-Path $payload 'HSRUnlocker.runtimeconfig.json')
    $native = [byte[]]::new(128)
    $native[0] = 0x4D; $native[1] = 0x5A; $native[0x3C] = 64
    $native[64] = 0x50; $native[65] = 0x45
    $native[68] = 0x64; $native[69] = 0x86
    $native[88] = 0x0B; $native[89] = 0x02
    [IO.File]::WriteAllBytes((Join-Path $payload 'Microsoft.WindowsAppRuntime.Bootstrap.dll'), $native)
}

function Reject-Legacy([string]$Name, [string[]]$Files) {
    Write-Json @{ schemaVersion = 1; files = $Files } $legacyPath
    $rejected = $false
    try { Get-LegacyRuntimeFiles $legacyPath $payload | Out-Null }
    catch { $rejected = $true }
    Check $rejected $Name
}

try {
    [IO.File]::WriteAllText($manifestPath, $baseline)
    $valid = Read-InstallerRuntimeManifest $manifestPath '2.5.1'
    Check ($valid.dotnet.version -eq '8.0.32' -and $valid.windowsAppRuntime.version -eq '2.5.1') 'Accepts the pinned Microsoft x64 runtime manifest'
    Reject-Manifest 'Rejects an unknown manifest schema' { param($m) $m.schemaVersion = 2 }
    Reject-Manifest 'Rejects a string manifest schema' { param($m) $m.schemaVersion = '1' }
    Reject-Manifest 'Rejects an invalid SHA-256' { param($m) $m.dotnet.sha256 = '0' * 63 }
    Reject-Manifest 'Rejects a missing SHA-256' { param($m) $m.dotnet.sha256 = $null }
    Reject-Manifest 'Rejects a non-HTTPS runtime URL' { param($m) $m.dotnet.url = $m.dotnet.url.Replace('https:', 'http:') }
    Reject-Manifest 'Rejects a foreign runtime download host' { param($m) $m.dotnet.url = $m.dotnet.url.Replace('builds.dotnet.microsoft.com', 'example.invalid') }
    Reject-Manifest 'Rejects credentials in a runtime URL' { param($m) $m.dotnet.url = $m.dotnet.url.Replace('https://', 'https://name@') }
    Reject-Manifest 'Rejects a runtime URL fragment' { param($m) $m.dotnet.url += '#fragment' }
    Reject-Manifest 'Rejects a runtime URL query' { param($m) $m.dotnet.url += '?alternate=true' }
    Reject-Manifest 'Rejects a nonstandard runtime URL port' { param($m) $m.dotnet.url = $m.dotnet.url.Replace('.com/', '.com:444/') }
    Reject-Manifest 'Rejects an unexpected installer filename' { param($m) $m.dotnet.url = $m.dotnet.url.Replace('win-x64.exe', 'win-arm64.exe') }
    Reject-Manifest 'Rejects an unsupported .NET major version' { param($m) $m.dotnet.version = '9.0.0' }
    Reject-Manifest 'Rejects a wrong runtime architecture' { param($m) $m.dotnet.architecture = 'arm64' }
    Reject-Manifest 'Rejects a wrong required .NET major version' { param($m) $m.dotnet.minimumVersion = '9.0.0' }
    Reject-Manifest 'Rejects minimum .NET newer than the pinned installer' { param($m) $m.dotnet.minimumVersion = '8.0.33' }
    Reject-Manifest 'Rejects an SDK/runtime version mismatch' { param($m) $m.windowsAppRuntime.version = '2.5.2' }
    Reject-Manifest 'Rejects an untrusted Windows App Runtime host' { param($m) $m.windowsAppRuntime.url = $m.windowsAppRuntime.url.Replace('download.microsoft.com', 'example.invalid') }
    Reject-Manifest 'Rejects a Windows App Runtime URL query' { param($m) $m.windowsAppRuntime.url += '?file=other' }
    Reject-Manifest 'Rejects an invalid runtime size' { param($m) $m.dotnet.bytes = -1 }

    Set-Payload $false
    Assert-FrameworkDependentPayload $payload
    Check $true 'Accepts an authored framework-dependent payload'
    Set-Payload $true
    $rejected = $false
    try { Assert-FrameworkDependentPayload $payload } catch { $rejected = $true }
    Check $rejected 'Rejects a self-contained runtime configuration'
    Set-Payload $false
    [IO.File]::WriteAllBytes((Join-Path $payload 'coreclr.dll'), [byte[]](1))
    $rejected = $false
    try { Assert-FrameworkDependentPayload $payload } catch { $rejected = $true }
    Check $rejected 'Rejects bundled runtime files in a dependent payload'
    Remove-Item -LiteralPath (Join-Path $payload 'coreclr.dll')
    Remove-Item -LiteralPath (Join-Path $payload 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
    $rejected = $false
    try { Assert-FrameworkDependentPayload $payload } catch { $rejected = $true }
    Check $rejected 'Rejects a payload missing its bootstrap DLL'
    Set-Payload $false
    $bootstrapPath = Join-Path $payload 'Microsoft.WindowsAppRuntime.Bootstrap.dll'
    $native = [IO.File]::ReadAllBytes($bootstrapPath)
    $native[68] = 0x4C; $native[69] = 0x01
    [IO.File]::WriteAllBytes($bootstrapPath, $native)
    $rejected = $false
    try { Assert-FrameworkDependentPayload $payload } catch { $rejected = $true }
    Check $rejected 'Rejects a bootstrap DLL with the wrong native architecture'
    Set-Payload $false

    Write-Json @{ schemaVersion = 1; files = @('coreclr.dll', 'zh-CN/System.Runtime.resources.dll',
        'ThirdPartyNotices/microsoft.windowsdesktop.app.runtime.win-x64/8.0.29/LICENSE') } $legacyPath
    $legacy = @(Get-LegacyRuntimeFiles $legacyPath $payload)
    Check ($legacy.Count -eq 3 -and $legacy -contains 'coreclr.dll') 'Accepts exact obsolete-runtime files and their old license'
    Reject-Legacy 'Rejects cleanup parent traversal' @('../outside.dll')
    Reject-Legacy 'Rejects rooted cleanup paths' @('C:/outside.dll')
    Reject-Legacy 'Rejects UNC cleanup paths' @('//server/share/file.dll')
    Reject-Legacy 'Rejects alternate-stream cleanup paths' @('runtime.dll:stream')
    Reject-Legacy 'Rejects mixed separators in cleanup paths' @('dir\../outside.dll')
    Reject-Legacy 'Rejects wildcard cleanup paths' @('*.dll')
    Reject-Legacy 'Rejects case-insensitive duplicate cleanup entries' @('coreclr.dll', 'CORECLR.dll')
    Reject-Legacy 'Rejects application files in cleanup inventory' @('HSRUnlocker.dll')
    Reject-Legacy 'Rejects settings in cleanup inventory' @('settings.json')
    Reject-Legacy 'Rejects root license in cleanup inventory' @('LICENSE')
    Reject-Legacy 'Rejects cleanup paths with trailing Windows dots' @('dir./runtime.dll')
    Reject-Legacy 'Rejects cleanup paths in the current payload' @('Microsoft.WindowsAppRuntime.Bootstrap.dll')
    $inventory = @(Get-LegacyRuntimeFiles (Join-Path $repo 'installer\legacy-runtime-files.json') '')
    Check ($inventory.Count -gt 0 -and $inventory -contains 'coreclr.dll') 'Validates the shipped obsolete-runtime inventory'
    Write-Host "Installer metadata tests: $passed passed, 0 failed."
}
finally {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $fixtureRoot).Path)
    if ([IO.Path]::GetFileName($resolved) -cne $fixtureName -or
        -not [string]::Equals([IO.Path]::GetDirectoryName($resolved), [IO.Path]::GetTempPath().TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing an unexpected fixture cleanup path.'
    }
    if (@(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) { throw 'Refusing a reparse point in the fixture.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

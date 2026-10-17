# Compiles and executes only a lowest-privilege fixture with synthetic callbacks.
[CmdletBinding()]
param(
    [string]$CompilerPath,
    [string]$EvidenceDir,
    [switch]$RedStub
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path ([IO.Path]::GetTempPath()) ('HSRUnlocker-LegacyFixture-' + [Guid]::NewGuid().ToString('N'))
}
$EvidenceDir = [IO.Path]::GetFullPath($EvidenceDir)
if (Test-Path -LiteralPath $EvidenceDir) {
    if (-not (Test-Path -LiteralPath $EvidenceDir -PathType Container) -or
        @(Get-ChildItem -LiteralPath $EvidenceDir -Force).Count -ne 0) {
        throw 'Fixture evidence output must be a new or empty directory.'
    }
}
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $CompilerPath = $command.Source }
    foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not [string]::IsNullOrWhiteSpace($CompilerPath)) { break }
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        foreach ($relative in @('Inno Setup 7\ISCC.exe', 'Programs\Inno Setup 7\ISCC.exe')) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CompilerPath = $candidate; break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    Write-Host 'SKIP: native legacy cleanup checks; Inno Setup 7.1 compiler is unavailable.'
    return
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf) -or
    [IO.Path]::GetFileName($CompilerPath) -ine 'ISCC.exe') {
    throw 'CompilerPath must identify an installed Inno Setup ISCC.exe.'
}
$compilerVersion = (& $CompilerPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [version]$compilerVersion -lt [version]'7.1.0') {
    throw 'Native cleanup fixture requires Inno Setup 7.1 or newer.'
}
$fixture = Join-Path $PSScriptRoot 'LegacyCleanup.fixture.iss'
$compilerArguments = @('--quiet', "--output-dir=$EvidenceDir")
if ($RedStub) { $compilerArguments += '/DLegacyRedStub' }
$compilerArguments += $fixture
& $CompilerPath @compilerArguments 2>&1 |
    Tee-Object -FilePath (Join-Path $EvidenceDir 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'The isolated cleanup Pascal fixture did not compile.' }
$resultFile = Join-Path $EvidenceDir 'results.txt'
$logFile = Join-Path $EvidenceDir 'fixture.log'
$executable = Join-Path $EvidenceDir 'LegacyCleanup.fixture.exe'
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/ResultFile=`"$resultFile`"", "/LOG=`"$logFile`"")
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw 'The isolated cleanup fixture exceeded its 30-second bound.'
}
if ($process.ExitCode -ne 0) { throw "Native cleanup fixture failed; see $logFile." }
$results = [IO.File]::ReadAllText($resultFile)
if ($results -notmatch '(?m)^CHECKS=(\d+)\r?$') { throw 'Native cleanup fixture did not report completed checks.' }
$checks = [int]$Matches[1]
if ($results -notmatch '(?m)^FAILURES=(\d+)\r?$') { throw 'Native cleanup fixture did not report its failure count.' }
$failures = [int]$Matches[1]
$report = [ordered]@{
    nativeChecks = $(if ($failures -eq 0) { 'passed' } else { 'failed' })
    checks = $checks; failures = $failures; compiler = $compilerVersion; redStub = [bool]$RedStub
    fixture = $fixture; evidence = $EvidenceDir
    liveRegistryRead = $false; productionInstallerExecuted = $false; applicationExecuted = $false
    syntheticFileAttributes = $true; productionCleanupPolicy = -not [bool]$RedStub
}
[IO.File]::WriteAllText((Join-Path $EvidenceDir 'legacy-fixture.json'),
    ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if ($failures -ne 0) { throw "$failures of $checks native legacy cleanup checks failed; see $resultFile." }
Write-Host "PASS: $checks actual Pascal legacy cleanup policy checks."

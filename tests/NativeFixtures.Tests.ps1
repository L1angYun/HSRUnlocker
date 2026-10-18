# Required CI entrypoint; only owned, lowest-privilege synthetic fixtures run.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CompilerPath,
    [string]$EvidenceDir
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    throw 'An explicit CompilerPath is required for native CI checks.'
}
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path ([IO.Path]::GetTempPath()) ('HSRUnlocker-NativeFixtures-' + [Guid]::NewGuid().ToString('N'))
}
$EvidenceDir = [IO.Path]::GetFullPath($EvidenceDir)
if (Test-Path -LiteralPath $EvidenceDir) {
    if (-not (Test-Path -LiteralPath $EvidenceDir -PathType Container) -or
        @(Get-ChildItem -LiteralPath $EvidenceDir -Force).Count -ne 0) {
        throw 'Native CI evidence output must be a new or empty directory.'
    }
}
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action }
    catch { $rejected = $true }
    if (-not $rejected) { throw "$Name was accepted." }
    Write-Host "PASS: $Name is rejected."
}

function Assert-ExpectedLegacyFailure([string]$Directory, [string]$FailureMessage, [int]$ExpectedChecks) {
    if ([string]::IsNullOrWhiteSpace($FailureMessage)) {
        throw 'RedStub unexpectedly succeeded.'
    }
    $reportFile = Join-Path $Directory 'legacy-fixture.json'
    $resultFile = Join-Path $Directory 'results.txt'
    if (-not (Test-Path -LiteralPath $reportFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
        throw 'RedStub did not produce completed fixture evidence.'
    }
    $report = Get-Content -LiteralPath $reportFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.nativeChecks -cne 'failed' -or $report.redStub -cne $true -or
        $report.productionCleanupPolicy -cne $false -or $report.failures -le 0 -or
        $report.failures -gt $report.checks -or $report.checks -ne $ExpectedChecks) {
        throw 'RedStub report does not describe the expected failing assertions.'
    }
    $expectedMessage = "$($report.failures) of $($report.checks) native legacy cleanup checks failed; see $resultFile."
    if ($FailureMessage -cne $expectedMessage) {
        throw 'RedStub failed for a reason other than the expected assertion exception.'
    }
    $lines = @([IO.File]::ReadAllLines($resultFile))
    if ($lines -cnotcontains "CHECKS=$($report.checks)" -or
        $lines -cnotcontains "FAILURES=$($report.failures)" -or
        @($lines | Where-Object { $_.StartsWith('FAIL: ', [StringComparison]::Ordinal) }).Count -ne $report.failures) {
        throw 'RedStub result counts do not match its completed report.'
    }
    foreach ($required in @(
        'FAIL: matching x64 uninstall metadata recognizes owned prior self-contained app',
        'FAIL: exact planned native runtime file is removed'
    )) {
        if ($lines -cnotcontains $required) { throw "RedStub did not exercise the required assertion: $required" }
    }
}

$missingCompiler = Join-Path $EvidenceDir 'missing-compiler\ISCC.exe'
foreach ($suite in @('Installer', 'LegacyCleanup')) {
    $gateEvidence = Join-Path $EvidenceDir ("missing-compiler-$suite")
    $message = ''
    try { & (Join-Path $PSScriptRoot "$suite.Tests.ps1") -CompilerPath $missingCompiler -EvidenceDir $gateEvidence }
    catch { $message = $_.Exception.Message }
    if ($message -cne 'CompilerPath must identify an installed Inno Setup ISCC.exe.' -or
        @(Get-ChildItem -LiteralPath $gateEvidence -Force).Count -ne 0) {
        throw "$suite did not fail closed on an explicitly missing compiler."
    }
    Write-Host "PASS: $suite rejects an explicitly missing compiler without skipping."
}

$gateDirectory = Join-Path $EvidenceDir 'negative-gate-regressions'
New-Item -ItemType Directory -Path $gateDirectory | Out-Null
Assert-Rejected {
    Assert-ExpectedLegacyFailure $gateDirectory 'The isolated cleanup Pascal fixture did not compile.' 80
} 'RedStub compilation/setup failure'

# Authored reports exercise the same gate used below for the real RedStub process.
$gateResultFile = Join-Path $gateDirectory 'results.txt'
$gateReportFile = Join-Path $gateDirectory 'legacy-fixture.json'
$gateReport = [ordered]@{
    nativeChecks = 'failed'; checks = 80; failures = 2
    redStub = $true; productionCleanupPolicy = $false
}
$requiredFailures = @(
    'FAIL: matching x64 uninstall metadata recognizes owned prior self-contained app',
    'FAIL: exact planned native runtime file is removed'
)
$gateResults = ($requiredFailures + @('CHECKS=80', 'FAILURES=2')) -join "`r`n"
$gateMessage = "2 of 80 native legacy cleanup checks failed; see $gateResultFile."
[IO.File]::WriteAllText($gateReportFile, ($gateReport | ConvertTo-Json))
[IO.File]::WriteAllText($gateResultFile, $gateResults)
Assert-ExpectedLegacyFailure $gateDirectory $gateMessage 80
Write-Host 'PASS: completed expected assertion failure is accepted.'
Assert-Rejected {
    Assert-ExpectedLegacyFailure $gateDirectory '' 80
} 'RedStub unexpected success'
Assert-Rejected {
    Assert-ExpectedLegacyFailure $gateDirectory 'The isolated cleanup Pascal fixture did not compile.' 80
} 'RedStub setup error with unrelated completed evidence'
foreach ($case in @(
    @{ Name = 'successful report'; Property = 'nativeChecks'; Value = 'passed' },
    @{ Name = 'non-RedStub report'; Property = 'redStub'; Value = $false },
    @{ Name = 'production-policy report'; Property = 'productionCleanupPolicy'; Value = $true },
    @{ Name = 'zero failures'; Property = 'failures'; Value = 0 },
    @{ Name = 'incomplete check count'; Property = 'checks'; Value = 79 }
)) {
    $invalid = $gateReport | ConvertTo-Json | ConvertFrom-Json
    $invalid.($case.Property) = $case.Value
    [IO.File]::WriteAllText($gateReportFile, ($invalid | ConvertTo-Json))
    Assert-Rejected {
        Assert-ExpectedLegacyFailure $gateDirectory $gateMessage 80
    } ("RedStub " + $case.Name)
}
[IO.File]::WriteAllText($gateReportFile, ($gateReport | ConvertTo-Json))
foreach ($required in $requiredFailures) {
    [IO.File]::WriteAllText($gateResultFile, $gateResults.Replace($required, 'FAIL: unrelated assertion'))
    Assert-Rejected {
        Assert-ExpectedLegacyFailure $gateDirectory $gateMessage 80
    } ("RedStub missing intended FAIL line '$required'")
}
[IO.File]::WriteAllText($gateResultFile, $gateResults.Replace('CHECKS=80', 'CHECKS=79'))
Assert-Rejected {
    Assert-ExpectedLegacyFailure $gateDirectory $gateMessage 80
} 'RedStub mismatched result counts'

$runtimeDirectory = Join-Path $EvidenceDir 'runtime'
& (Join-Path $PSScriptRoot 'Installer.Tests.ps1') -CompilerPath $CompilerPath -EvidenceDir $runtimeDirectory
$runtime = Get-Content -LiteralPath (Join-Path $runtimeDirectory 'runtime-fixture.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($runtime.nativeChecks -cne 'passed' -or $runtime.checks -le 0 -or
    $runtime.productionInstallerExecuted -cne $false -or $runtime.runtimeInstallerExecuted -cne $false) {
    throw 'The prerequisite fixture did not complete its required synthetic checks.'
}

$legacyDirectory = Join-Path $EvidenceDir 'legacy'
& (Join-Path $PSScriptRoot 'LegacyCleanup.Tests.ps1') -CompilerPath $CompilerPath -EvidenceDir $legacyDirectory
$legacy = Get-Content -LiteralPath (Join-Path $legacyDirectory 'legacy-fixture.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($legacy.nativeChecks -cne 'passed' -or $legacy.checks -le 0 -or $legacy.failures -ne 0 -or
    $legacy.redStub -cne $false -or $legacy.productionCleanupPolicy -cne $true) {
    throw 'The legacy cleanup fixture did not complete its required production-policy checks.'
}

# Every child starts absent under a new/empty evidence root, so stale reports cannot satisfy this gate.
$redDirectory = Join-Path $EvidenceDir 'legacy-red'
$redMessage = ''
try {
    & (Join-Path $PSScriptRoot 'LegacyCleanup.Tests.ps1') -CompilerPath $CompilerPath -EvidenceDir $redDirectory -RedStub
}
catch { $redMessage = $_.Exception.Message }
Assert-ExpectedLegacyFailure $redDirectory $redMessage $legacy.checks
Write-Host "PASS: actual RedStub assertion failure is verified: $redMessage"
Write-Host "Native fixture CI checks passed; evidence: $EvidenceDir"

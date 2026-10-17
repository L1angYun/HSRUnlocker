# Compile and execute only an isolated, lowest-privilege Pascal callback fixture.
[CmdletBinding()]
param(
    [string]$CompilerPath,
    [string]$EvidenceDir
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path ([IO.Path]::GetTempPath()) ('HSRUnlocker-RuntimeFixture-' + [Guid]::NewGuid().ToString('N'))
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
    $compilerRoots = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)})
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $compilerRoots += Join-Path $env:LOCALAPPDATA 'Programs'
    }
    foreach ($root in $compilerRoots) {
        if (-not [string]::IsNullOrWhiteSpace($CompilerPath)) { break }
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidate = Join-Path $root 'Inno Setup 7\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CompilerPath = $candidate }
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    Write-Host 'SKIP: native prerequisite workflow checks; Inno Setup 7.1 compiler is unavailable.'
    [pscustomobject]@{ nativeChecks = 'skipped'; reason = 'Inno Setup compiler unavailable' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDir 'runtime-fixture.json') -Encoding UTF8
    return
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf) -or
    [IO.Path]::GetFileName($CompilerPath) -ine 'ISCC.exe') {
    throw 'CompilerPath must identify an installed Inno Setup ISCC.exe.'
}
$compilerVersion = (& $CompilerPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [version]$compilerVersion -lt [version]'7.1.0') {
    throw 'Native fixture checks require Inno Setup 7.1 or newer.'
}
$fixture = Join-Path $PSScriptRoot 'RuntimePrerequisites.fixture.iss'
& $CompilerPath --quiet "--output-dir=$EvidenceDir" $fixture 2>&1 |
    Tee-Object -FilePath (Join-Path $EvidenceDir 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'The isolated Pascal fixture did not compile.' }
$resultFile = Join-Path $EvidenceDir 'results.txt'
$logFile = Join-Path $EvidenceDir 'fixture.log'
$executable = Join-Path $EvidenceDir 'RuntimePrerequisites.fixture.exe'
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/ResultFile=`"$resultFile`"", "/LOG=`"$logFile`"")
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw 'The isolated prerequisite fixture exceeded its 30-second bound.'
}
if ($process.ExitCode -ne 0) { throw "Actual Pascal workflow assertions failed; see $logFile." }
$results = [IO.File]::ReadAllText($resultFile)
if ($results -notmatch '(?m)^CHECKS=(\d+)\r?$') { throw 'The native fixture did not report completed checks.' }
$checks = [int]$Matches[1]
$report = [ordered]@{
    nativeChecks = 'passed'; checks = $checks; compiler = $compilerVersion
    fixture = $fixture; evidence = $EvidenceDir
    liveRegistryRead = $false; runtimeInstallerExecuted = $false
    productionInstallerExecuted = $false; applicationExecuted = $false
}
[IO.File]::WriteAllText((Join-Path $EvidenceDir 'runtime-fixture.json'),
    ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "PASS: $checks actual Pascal prerequisite workflow checks."

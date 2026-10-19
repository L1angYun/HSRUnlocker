# Exercise release staging with authored local bytes only; never run an installer.
#Requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helper = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Prepare-ReleaseAssets.ps1'
$workspaceName = 'HSRUnlocker-ReleaseAssetsTests-' + [Guid]::NewGuid().ToString('N')
$workspace = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $workspaceName))
$results = [Collections.Generic.List[object]]::new()
$script:caseNumber = 0
$installerName = 'HSRUnlocker-1.3.6-win-x64-Setup.exe'
$sourceName = 'HSRUnlocker-1.3.6-source.zip'
$installerHash = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
$sourceHash = '868b4d560d5a90e7abcfd4e208d4689e1be178ec85cdec6400e19dbf8f919b4d'
$manifestHash = '9a56bdb360347f55239f931a82b4000f43ad4e845de97eeb29deadf40566f497'
$manifestText = '{"tag_name":"v1.3.6","draft":false,"prerelease":false,"assets":[{"name":"HSRUnlocker-1.3.6-win-x64-Setup.exe","size":3,"digest":"sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}]}'
$zipBytes = [Convert]::FromBase64String('UEsDBBQAAAAAAAAAIVyJV7Q3DgAAAA4AAAAKAAAAUkVBRE1FLnR4dGZpeHR1cmUgc291cmNlUEsBAhQAFAAAAAAAAAAhXIlXtDcOAAAADgAAAAoAAAAAAAAAAAAAAAAAAAAAAFJFQURNRS50eHRQSwUGAAAAAAEAAQA4AAAANgAAAAAA')
New-Item -ItemType Directory -Path $workspace | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message (expected '$Expected', got '$Actual')." }
}

function New-Fixture {
    $script:caseNumber++
    $root = Join-Path $workspace ('case-' + $script:caseNumber + ' with spaces')
    $inputs = Join-Path $root 'inputs'
    New-Item -ItemType Directory -Path $inputs -Force | Out-Null
    $installer = Join-Path $inputs $installerName
    $source = Join-Path $inputs 'reviewed-source.zip'
    $manifest = Join-Path $inputs 'update.json'
    [IO.File]::WriteAllBytes($installer, [Text.Encoding]::ASCII.GetBytes('abc'))
    [IO.File]::WriteAllBytes($source, $zipBytes)
    [IO.File]::WriteAllBytes($manifest, [Text.Encoding]::UTF8.GetBytes($manifestText))
    [pscustomobject]@{
        Root = $root; Inputs = $inputs; Installer = $installer; Source = $source
        Manifest = $manifest; Output = Join-Path $root 'release-assets'; Version = '1.3.6'
    }
}

function Invoke-Helper($Fixture) {
    Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
    & $helper -Version $Fixture.Version -InstallerPath $Fixture.Installer `
        -SourceArchivePath $Fixture.Source -OutDir $Fixture.Output
}

function Assert-Rejected($Fixture) {
    Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
    $rejected = $false
    try { Invoke-Helper $Fixture | Out-Null }
    catch { $rejected = $true }
    Assert-True $rejected 'Unsafe or inconsistent release inputs were accepted.'
    Assert-True (-not (Test-Path -LiteralPath $Fixture.Output)) 'Rejected inputs left staged output.'
}

function Write-Manifest($Fixture, $Value) {
    [IO.File]::WriteAllText($Fixture.Manifest, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Invoke-Test([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true })
        Write-Host "PASS $Name"
    }
    catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false })
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

try {
    Invoke-Test 'Stages exactly four release files without changing reviewed input bytes' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText((Join-Path $fixture.Inputs 'private-build.pdb'), 'Do not stage this file.')
        $before = @{}
        foreach ($path in @($fixture.Installer, $fixture.Source, $fixture.Manifest)) {
            $before[$path] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
        }
        Invoke-Helper $fixture | Out-Null
        $names = @(Get-ChildItem -LiteralPath $fixture.Output -Force | Sort-Object Name | ForEach-Object Name)
        Assert-Equal ($names -join ',') "$sourceName,$installerName,SHA256SUMS.txt,update.json" 'Release file set differs'
        foreach ($pair in @(
            @($fixture.Installer, $installerName), @($fixture.Source, $sourceName), @($fixture.Manifest, 'update.json')
        )) {
            Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $fixture.Output $pair[1])))) $before[$pair[0]] 'Staged bytes changed'
            Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pair[0]))) $before[$pair[0]] 'Input bytes changed'
        }
        $manifestBytes = [IO.File]::ReadAllBytes((Join-Path $fixture.Output 'update.json'))
        $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($manifestBytes))
        try { Assert-Equal $document.RootElement.GetProperty('tag_name').GetString() 'v1.3.6' 'Staged manifest is not readable by the client parser' }
        finally { $document.Dispose() }
    }
    Invoke-Test 'Emits three literal known SHA-256 checksums with two spaces and no BOM' {
        $fixture = New-Fixture
        Invoke-Helper $fixture | Out-Null
        $expected = "$installerHash  $installerName`n$sourceHash  $sourceName`n$manifestHash  update.json`n"
        $actual = [IO.File]::ReadAllBytes((Join-Path $fixture.Output 'SHA256SUMS.txt'))
        Assert-Equal ([Convert]::ToBase64String($actual)) ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expected))) 'Checksum bytes differ'
    }
    Invoke-Test 'Accepts an existing empty output directory' {
        $fixture = New-Fixture
        New-Item -ItemType Directory -Path $fixture.Output | Out-Null
        Invoke-Helper $fixture | Out-Null
        Assert-Equal @(Get-ChildItem -LiteralPath $fixture.Output -Force).Count 4 'Empty output was not populated'
    }
    Invoke-Test 'Returns one structured summary describing all four staged files' {
        $fixture = New-Fixture
        $summary = @(Invoke-Helper $fixture)
        Assert-Equal $summary.Count 1 'Helper emitted unexpected pipeline output'
        Assert-Equal $summary[0].Version '1.3.6' 'Summary version differs'
        Assert-Equal $summary[0].OutputDirectory $fixture.Output 'Summary output differs'
        Assert-Equal @($summary[0].Files).Count 4 'Summary file inventory is incomplete'
        foreach ($entry in $summary[0].Files) {
            Assert-Equal $entry.Path (Join-Path $fixture.Output $entry.Name) 'Summary file path differs'
            Assert-Equal $entry.Size (Get-Item -LiteralPath $entry.Path).Length 'Summary size differs'
            Assert-Equal $entry.SHA256 ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Summary checksum differs'
        }
    }
    Invoke-Test 'Resolves relative paths from the PowerShell location without changing the process directory' {
        $fixture = New-Fixture
        $processDirectory = [Environment]::CurrentDirectory
        Push-Location -LiteralPath $fixture.Root
        try {
            Assert-True (-not [string]::Equals((Get-Location).ProviderPath, $processDirectory, [StringComparison]::OrdinalIgnoreCase)) 'Fixture location must differ from the process directory.'
            $summary = & $helper -Version '1.3.6' -InstallerPath (Join-Path 'inputs' $installerName) `
                -SourceArchivePath 'inputs\reviewed-source.zip' -OutDir 'release-assets'
            Assert-Equal $summary.OutputDirectory $fixture.Output 'Relative output resolved outside the PowerShell location'
            $names = @(Get-ChildItem -LiteralPath $fixture.Output -Force | Sort-Object Name | ForEach-Object Name)
            Assert-Equal ($names -join ',') "$sourceName,$installerName,SHA256SUMS.txt,update.json" 'Relative paths did not stage the four expected files'
        }
        finally {
            Pop-Location
            $after = [Environment]::CurrentDirectory
            if ($after -cne $processDirectory) { [Environment]::CurrentDirectory = $processDirectory }
            Assert-Equal $after $processDirectory 'Helper changed the process current directory'
        }
    }
    foreach ($parameterName in @('InstallerPath', 'SourceArchivePath', 'OutDir')) {
        Invoke-Test "Rejects a non-filesystem provider for $parameterName with a clear error" {
            $fixture = New-Fixture
            $parameters = @{
                Version = $fixture.Version; InstallerPath = $fixture.Installer
                SourceArchivePath = $fixture.Source; OutDir = $fixture.Output
            }
            $parameters[$parameterName] = 'Env:\PATH'
            $message = $null
            try { & $helper @parameters | Out-Null }
            catch { $message = $_.Exception.Message }
            Assert-True ($null -ne $message -and $message.Contains('FileSystem')) 'A non-filesystem path did not receive a clear FileSystem provider error.'
            Assert-True (-not (Test-Path -LiteralPath $fixture.Output)) 'Rejected provider path left staged output.'
        }
    }
    foreach ($invalidVersion in @('v1.3.6', '1.3', '1.3.6.0', '1.3.6-preview', '1.3.6+build', '01.3.6', ' 1.3.6', "1.3.6`n", ([string][char]0x0661 + '.3.6'))) {
        Invoke-Test "Rejects noncanonical stable version '$($invalidVersion.Replace("`n", '\n'))'" {
            $fixture = New-Fixture
            $fixture.Version = $invalidVersion
            Assert-Rejected $fixture
        }
    }
    Invoke-Test 'Rejects changed installer bytes even when the length is unchanged' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText($fixture.Installer, 'abd')
        Assert-Rejected $fixture
    }
    foreach ($inputKind in @('Installer', 'Source', 'Manifest')) {
        Invoke-Test "Rejects missing $inputKind input" {
            $fixture = New-Fixture
            Remove-Item -LiteralPath $fixture.$inputKind
            Assert-Rejected $fixture
        }
        Invoke-Test "Rejects empty $inputKind input" {
            $fixture = New-Fixture
            [IO.File]::WriteAllBytes($fixture.$inputKind, [byte[]]::new(0))
            Assert-Rejected $fixture
        }
        Invoke-Test "Rejects a directory used as $inputKind input" {
            $fixture = New-Fixture
            Remove-Item -LiteralPath $fixture.$inputKind
            New-Item -ItemType Directory -Path $fixture.$inputKind | Out-Null
            Assert-Rejected $fixture
        }
    }
    Invoke-Test 'Rejects an unexpected installer filename' {
        $fixture = New-Fixture
        $renamed = Join-Path $fixture.Inputs 'Other-Setup.exe'
        Move-Item -LiteralPath $fixture.Installer -Destination $renamed
        $fixture.Installer = $renamed
        Assert-Rejected $fixture
    }
    Invoke-Test 'Rejects a non-ZIP source archive' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText($fixture.Source, 'This is not a ZIP archive.')
        Assert-Rejected $fixture
    }
    Invoke-Test 'Rejects a valid ZIP without source entries' {
        $fixture = New-Fixture
        [IO.File]::WriteAllBytes($fixture.Source, [Convert]::FromBase64String('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA=='))
        Assert-Rejected $fixture
    }
    Invoke-Test 'Rejects a ZIP with unreadable entry data' {
        $fixture = New-Fixture
        $invalidZip = [byte[]]$zipBytes.Clone()
        $invalidZip[8] = 99
        $invalidZip[64] = 99
        [IO.File]::WriteAllBytes($fixture.Source, $invalidZip)
        Assert-Rejected $fixture
    }
    Invoke-Test 'Rejects a ZIP entry whose declared length exceeds the readable data' {
        $fixture = New-Fixture
        $invalidZip = [byte[]]$zipBytes.Clone()
        # This authored fixture stores compressed length at central-directory offset 74.
        foreach ($offset in 74..77) { $invalidZip[$offset] = 0 }
        [IO.File]::WriteAllBytes($fixture.Source, $invalidZip)
        Assert-Rejected $fixture
    }
    foreach ($manifestCase in @(
        @{ Name = 'mismatched release tag'; Change = { param($m) $m.tag_name = 'v1.3.5' } },
        @{ Name = 'draft release'; Change = { param($m) $m.draft = $true } },
        @{ Name = 'prerelease'; Change = { param($m) $m.prerelease = $true } },
        @{ Name = 'string draft flag'; Change = { param($m) $m.draft = 'false' } },
        @{ Name = 'null prerelease flag'; Change = { param($m) $m.prerelease = $null } },
        @{ Name = 'missing stable flag'; Change = { param($m) $m.PSObject.Properties.Remove('draft') } },
        @{ Name = 'unknown root field'; Change = { param($m) $m | Add-Member extra 'unexpected' } },
        @{ Name = 'no installer entry'; Change = { param($m) $m.assets = @() } },
        @{ Name = 'multiple installer entries'; Change = { param($m) $m.assets = @($m.assets[0], $m.assets[0]) } },
        @{ Name = 'asset object instead of array'; Change = { param($m) $m.assets = $m.assets[0] } },
        @{ Name = 'wrong installer entry name'; Change = { param($m) $m.assets[0].name = 'Other-Setup.exe' } },
        @{ Name = 'wrong installer entry case'; Change = { param($m) $m.assets[0].name = $m.assets[0].name.ToUpperInvariant() } },
        @{ Name = 'wrong installer length'; Change = { param($m) $m.assets[0].size = 4 } },
        @{ Name = 'string installer length'; Change = { param($m) $m.assets[0].size = '3' } },
        @{ Name = 'fractional installer length'; Change = { param($m) $m.assets[0].size = 3.5 } },
        @{ Name = 'zero installer length'; Change = { param($m) $m.assets[0].size = 0 } },
        @{ Name = 'wrong installer digest'; Change = { param($m) $m.assets[0].digest = 'sha256:' + ('0' * 64) } },
        @{ Name = 'uppercase digest'; Change = { param($m) $m.assets[0].digest = $m.assets[0].digest.ToUpperInvariant() } },
        @{ Name = 'missing digest'; Change = { param($m) $m.assets[0].PSObject.Properties.Remove('digest') } },
        @{ Name = 'unknown installer field'; Change = { param($m) $m.assets[0] | Add-Member url 'https://example.invalid/ignored' } }
    )) {
        Invoke-Test "Rejects manifest with $($manifestCase.Name)" {
            $fixture = New-Fixture
            $value = $manifestText | ConvertFrom-Json
            & $manifestCase.Change $value
            Write-Manifest $fixture $value
            Assert-Rejected $fixture
        }
    }
    foreach ($invalidJson in @(
        'not json', 'null', '[]', '{}',
        $manifestText.Replace('"tag_name":', '"Tag_Name":'),
        $manifestText.Replace('"draft":false', '"draft":true,"draft":false'),
        $manifestText.Replace('"size":3', '"size":4,"size":3')
    )) {
        Invoke-Test 'Rejects malformed, non-object, duplicate-key, or wrong-case manifest schema' {
            $fixture = New-Fixture
            [IO.File]::WriteAllText($fixture.Manifest, $invalidJson)
            Assert-Rejected $fixture
        }
    }
    foreach ($encodingCase in @(
        @{ Name = 'UTF-8 BOM'; Bytes = ([byte[]](239, 187, 191) + [Text.Encoding]::UTF8.GetBytes($manifestText)) },
        @{ Name = 'UTF-16 BOM'; Bytes = ([byte[]](255, 254) + [Text.Encoding]::Unicode.GetBytes($manifestText)) },
        @{ Name = 'invalid UTF-8'; Bytes = ([byte[]](255) + [Text.Encoding]::UTF8.GetBytes($manifestText)) }
    )) {
        Invoke-Test "Rejects manifest bytes with $($encodingCase.Name)" {
            $fixture = New-Fixture
            [IO.File]::WriteAllBytes($fixture.Manifest, $encodingCase.Bytes)
            Assert-Rejected $fixture
        }
    }
    Invoke-Test 'Rejects an oversized manifest before creating output' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText($fixture.Manifest, ($manifestText + (' ' * 65536)))
        Assert-Rejected $fixture
    }
    Invoke-Test 'Leaves a nonempty output directory untouched' {
        $fixture = New-Fixture
        New-Item -ItemType Directory -Path $fixture.Output | Out-Null
        $keep = Join-Path $fixture.Output 'keep.txt'
        [IO.File]::WriteAllText($keep, 'Keep these existing bytes.')
        Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
        $rejected = $false
        try { Invoke-Helper $fixture | Out-Null } catch { $rejected = $true }
        Assert-True $rejected 'A populated output directory was accepted.'
        Assert-Equal ([IO.File]::ReadAllText($keep)) 'Keep these existing bytes.' 'Existing output changed'
        Assert-Equal @(Get-ChildItem -LiteralPath $fixture.Output -Force).Count 1 'Files were added to existing output'
    }
    Invoke-Test 'Leaves an existing output file untouched' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText($fixture.Output, 'Keep this existing file.')
        Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
        $rejected = $false
        try { Invoke-Helper $fixture | Out-Null } catch { $rejected = $true }
        Assert-True $rejected 'An output file was accepted as a directory.'
        Assert-Equal ([IO.File]::ReadAllText($fixture.Output)) 'Keep this existing file.' 'Existing output file changed'
    }
    Invoke-Test 'Rejects output inside an input directory' {
        $fixture = New-Fixture
        $fixture.Output = Join-Path $fixture.Inputs 'nested-output'
        Assert-Rejected $fixture
    }
    Invoke-Test 'Rejects an output path occupied by an input file' {
        $fixture = New-Fixture
        $fixture.Output = $fixture.Installer
        Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
        $rejected = $false
        try { Invoke-Helper $fixture | Out-Null } catch { $rejected = $true }
        Assert-True $rejected 'An input file was accepted as output.'
        Assert-Equal ([IO.File]::ReadAllText($fixture.Installer)) 'abc' 'Overlapping installer changed'
    }
    foreach ($inputKind in @('Installer', 'Source')) {
        Invoke-Test "Rejects $inputKind input under a reparse-point directory" {
            $fixture = New-Fixture
            $junction = Join-Path $fixture.Root 'input-link'
            New-Item -ItemType Junction -Path $junction -Value $fixture.Inputs | Out-Null
            try {
                $fixture.$inputKind = Join-Path $junction ([IO.Path]::GetFileName($fixture.$inputKind))
                Assert-Rejected $fixture
            }
            finally { [IO.Directory]::Delete($junction) }
        }
    }
    Invoke-Test 'Rejects output under a reparse-point ancestor without writing to its target' {
        $fixture = New-Fixture
        $target = Join-Path $fixture.Root 'external-output'
        New-Item -ItemType Directory -Path $target | Out-Null
        $junction = Join-Path $fixture.Root 'output-link'
        New-Item -ItemType Junction -Path $junction -Value $target | Out-Null
        try {
            $fixture.Output = Join-Path $junction 'staged'
            Assert-Rejected $fixture
            Assert-Equal @(Get-ChildItem -LiteralPath $target -Force).Count 0 'Output traversed a reparse point'
        }
        finally { [IO.Directory]::Delete($junction) }
    }
    Invoke-Test 'Command-line validation failure exits nonzero and creates no output' {
        $fixture = New-Fixture
        Assert-True (Test-Path -LiteralPath $helper -PathType Leaf) 'Release asset helper is not implemented.'
        $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $helper -Version '1.3.6' `
            -InstallerPath $fixture.Installer -SourceArchivePath (Join-Path $fixture.Root 'missing.zip') `
            -OutDir $fixture.Output 2>&1
        Assert-True ($LASTEXITCODE -ne 0) 'Command-line validation failure exited successfully.'
        Assert-True (-not (Test-Path -LiteralPath $fixture.Output)) 'Command-line rejection left output.'
    }
}
finally {
    $item = Get-Item -LiteralPath $workspace -Force
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $workspace).Path)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [IO.Path]::GetFileName($resolved) -cne $workspaceName -or
        -not [string]::Equals([IO.Path]::GetDirectoryName($resolved), $expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove an unexpected release test workspace.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count -gt 0) {
        foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing to remove a reparse point in the release test workspace.'
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("Release asset tests: {0} passed, {1} failed." -f ($results.Count - $failed.Count), $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0

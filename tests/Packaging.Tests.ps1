# Exercise notice collection with authored temporary NuGet packages only.
[CmdletBinding()]
param(
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$collector = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Copy-ThirdPartyNotices.ps1'
$workspaceName = 'HSRUnlocker-PackagingTests-' + [Guid]::NewGuid().ToString('N')
$workspace = Join-Path ([IO.Path]::GetTempPath()) $workspaceName
$results = New-Object 'System.Collections.Generic.List[object]'
$script:caseNumber = 0
New-Item -ItemType Directory -Path $workspace | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message (expected '$Expected', got '$Actual')." }
}

function Assert-Throws([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null }
    catch { $failed = $true }
    Assert-True $failed 'Unsafe or incomplete input was accepted.'
}

function Write-FixtureFile([string]$Root, [string]$Relative, [string]$Content) {
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function New-Fixture {
    $script:caseNumber++
    $root = Join-Path $workspace ('case-' + $script:caseNumber)
    $cache = Join-Path $root 'packages'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    Write-FixtureFile $cache 'example.library\1.2.3\example.library.nuspec' @'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Example.Library</id><version>1.2.3</version>
    <license type="file">legal/Terms.txt</license>
    <licenseUrl>https://example.invalid/library-license</licenseUrl>
  </metadata>
</package>
'@
    Write-FixtureFile $cache 'example.library\1.2.3\legal\Terms.txt' 'Authored library terms.'
    Write-FixtureFile $cache 'example.library\1.2.3\NOTICE.txt' 'Authored root notice.'
    Write-FixtureFile $cache 'example.library\1.2.3\tools\ThirdPartyNotices.TXT' 'Authored nested notice.'
    Write-FixtureFile $cache 'example.library\1.2.3\lib\example.dll' 'Not a real binary; do not copy.'
    Write-FixtureFile $cache 'example.reference\2.0.0\example.reference.nuspec' @'
<?xml version="1.0" encoding="utf-8"?>
<package><metadata>
  <id>Example.Reference</id><version>2.0.0</version>
  <licenseUrl>https://example.invalid/reference-license</licenseUrl>
</metadata></package>
'@
    Write-FixtureFile $cache 'example.runtime.win-x64\8.0.0\example.runtime.win-x64.nuspec' @'
<?xml version="1.0" encoding="utf-8"?>
<package><metadata>
  <id>Example.Runtime.win-x64</id><version>8.0.0</version>
  <license type="expression">MIT</license>
  <licenseUrl>https://example.invalid/runtime-license</licenseUrl>
</metadata></package>
'@
    Write-FixtureFile $cache 'example.runtime.win-x64\8.0.0\LICENSE.TXT' 'Authored runtime license.'
    Write-FixtureFile $cache 'example.runtime.win-x64\8.0.0\THIRD-PARTY-NOTICES.TXT' 'Authored runtime third-party notice.'
    $folders = [ordered]@{}
    $folders[$cache + [IO.Path]::DirectorySeparatorChar] = @{}
    $assets = [ordered]@{
        version = 3
        packageFolders = $folders
        libraries = [ordered]@{
            'Example.Library/1.2.3' = [ordered]@{
                type = 'package'; path = 'example.library/1.2.3'
                files = @('example.library.nuspec', 'NOTICE.txt', 'tools/ThirdPartyNotices.TXT', 'legal/Terms.txt', 'lib/example.dll')
            }
            'Example.SourceProject/1.0.0' = @{ type = 'project'; path = '../source-project.csproj' }
        }
        project = [ordered]@{
            frameworks = [ordered]@{
                'net8.0-windows' = [ordered]@{
                    downloadDependencies = @(
                        @{ name = 'Example.Reference'; version = '[2.0.0, 2.0.0]' }
                        @{ name = 'Example.Runtime.win-x64'; version = '[8.0.0]' }
                    )
                }
            }
        }
    }
    [pscustomobject]@{
        Root = $root
        Cache = $cache
        Assets = $assets
        AssetsPath = Join-Path $root 'project.assets.json'
        Output = Join-Path $root 'notices'
    }
}

function Invoke-Collector($Fixture, [switch]$SelfContained) {
    [IO.File]::WriteAllText($Fixture.AssetsPath, ($Fixture.Assets | ConvertTo-Json -Depth 20),
        (New-Object Text.UTF8Encoding($false)))
    if (-not (Test-Path -LiteralPath $collector -PathType Leaf)) {
        throw 'Notice collector is not implemented; dependency license texts are missing.'
    }
    & $collector -AssetsFile $Fixture.AssetsPath -OutDir $Fixture.Output -SelfContained:$SelfContained | Out-Null
}

function Read-Manifest($Fixture) {
    Get-Content -LiteralPath (Join-Path $Fixture.Output 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Invoke-Test([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Error = $null })
        Write-Host "PASS $Name"
    }
    catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Error = $_.Exception.Message })
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

try {
    Invoke-Test 'Copies declared license and nested notices without binaries' {
        $fixture = New-Fixture
        Invoke-Collector $fixture
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'example.library\1.2.3\legal\Terms.txt'))) 'Authored library terms.' 'Declared license changed'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'example.library\1.2.3\tools\ThirdPartyNotices.TXT'))) 'Authored nested notice.' 'Nested notice changed'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'example.library\1.2.3\NOTICE.txt'))) 'Authored root notice.' 'Root notice changed'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Output 'example.library\1.2.3\lib\example.dll'))) 'An unrelated file was redistributed.'
        $package = @((Read-Manifest $fixture).packages | Where-Object { $_.id -eq 'Example.Library' })[0]
        Assert-Equal $package.license.type 'file' 'File license metadata was lost'
        Assert-Equal $package.license.value 'legal/Terms.txt' 'Declared relative license source was lost'
        Assert-Equal @($package.copiedFiles).Count 3 'Copied-file inventory is incomplete'
    }
    Invoke-Test 'Inventories URL-only framework download dependencies honestly' {
        $fixture = New-Fixture
        Invoke-Collector $fixture
        $packages = @((Read-Manifest $fixture).packages)
        Assert-Equal $packages.Count 3 'Package-only or project references were miscounted'
        $package = @($packages | Where-Object { $_.id -eq 'Example.Reference' })[0]
        Assert-Equal $package.license.url 'https://example.invalid/reference-license' 'License URL changed'
        Assert-True $package.embeddedTextMissing 'A URL was presented as embedded license text.'
        Assert-Equal @($package.copiedFiles).Count 0 'URL-only package received fabricated text'
    }
    Invoke-Test 'Includes runtime download licenses and explicit coverage boundaries' {
        $fixture = New-Fixture
        Invoke-Collector $fixture -SelfContained
        $manifest = Read-Manifest $fixture
        $package = @($manifest.packages | Where-Object { $_.id -eq 'Example.Runtime.win-x64' })[0]
        Assert-Equal $package.license.type 'expression' 'SPDX expression source was lost'
        Assert-Equal $package.license.value 'MIT' 'SPDX expression changed'
        Assert-Equal @($package.copiedFiles).Count 2 'Runtime notices were omitted'
        Assert-True $manifest.scope.selfContainedRequested 'Self-contained mode was not recorded.'
        Assert-True (@($manifest.scope.manualReview).Count -gt 0) 'Unlisted runtime/SDK coverage boundary is missing.'
    }
    Invoke-Test 'Manifest contains no machine paths and only valid copied-file references' {
        $fixture = New-Fixture
        Invoke-Collector $fixture
        $text = [IO.File]::ReadAllText((Join-Path $fixture.Output 'manifest.json'))
        Assert-True (-not $text.Contains($fixture.Root)) 'Manifest leaked a fixture path.'
        Assert-True (-not ($text -match '(?i)(^|["\s])([a-z]:[/\\]|\\\\)|file:')) 'Manifest contains a local absolute path.'
        foreach ($package in (Read-Manifest $fixture).packages) {
            foreach ($relative in $package.copiedFiles) {
                Assert-True (-not [IO.Path]::IsPathRooted($relative)) 'Copied-file reference is absolute.'
                Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Output $relative) -PathType Leaf) 'Manifest points to an absent copied file.'
            }
        }
    }
    Invoke-Test 'Deduplicates a package present in both assets sources' {
        $fixture = New-Fixture
        $fixture.Assets.project.frameworks.'net8.0-windows'.downloadDependencies += @{ name = 'example.library'; version = '[1.2.3, 1.2.3]' }
        Invoke-Collector $fixture
        $packages = @((Read-Manifest $fixture).packages | Where-Object { $_.id -eq 'Example.Library' })
        Assert-Equal $packages.Count 1 'The same package was inventoried twice'
        Assert-Equal @($packages[0].origins).Count 2 'An assets origin was omitted'
    }
    Invoke-Test 'Preserves distinct packages and versions with the same notice filename' {
        $fixture = New-Fixture
        Write-FixtureFile $fixture.Cache 'example.library\2.0.0\example.library.nuspec' '<package><metadata><id>Example.Library</id><version>2.0.0</version><license type="expression">MIT</license></metadata></package>'
        Write-FixtureFile $fixture.Cache 'example.library\2.0.0\NOTICE.txt' 'Authored second-version notice.'
        Write-FixtureFile $fixture.Cache 'other.library\1.0.0\other.library.nuspec' '<package><metadata><id>Other.Library</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>'
        Write-FixtureFile $fixture.Cache 'other.library\1.0.0\NOTICE.txt' 'Authored other-package notice.'
        $fixture.Assets.libraries['Example.Library/2.0.0'] = @{ type = 'package'; path = 'example.library/2.0.0' }
        $fixture.Assets.libraries['Other.Library/1.0.0'] = @{ type = 'package'; path = 'other.library/1.0.0' }
        Invoke-Collector $fixture
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'example.library\1.2.3\NOTICE.txt'))) 'Authored root notice.' 'Original-version notice was overwritten'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'example.library\2.0.0\NOTICE.txt'))) 'Authored second-version notice.' 'Second-version notice was overwritten'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'other.library\1.0.0\NOTICE.txt'))) 'Authored other-package notice.' 'Other-package notice was overwritten'
    }
    Invoke-Test 'Finds a legitimate fallback cache and preserves notice bytes' {
        $fixture = New-Fixture
        $folders = [ordered]@{}
        $folders[(Join-Path $fixture.Root 'absent-cache')] = @{}
        $folders[$fixture.Cache] = @{}
        $fixture.Assets.packageFolders = $folders
        $bytes = [byte[]]@(239, 187, 191, 76, 69, 71, 65, 76, 13, 10)
        $source = Join-Path $fixture.Cache 'example.library\1.2.3\tools\sdk_license.txt'
        [IO.File]::WriteAllBytes($source, $bytes)
        Invoke-Collector $fixture
        $actual = [IO.File]::ReadAllBytes((Join-Path $fixture.Output 'example.library\1.2.3\tools\sdk_license.txt'))
        Assert-Equal ([Convert]::ToBase64String($actual)) '77u/TEVHQUwNCg==' 'License bytes or line endings changed'
    }
    Invoke-Test 'Reads UTF-8 assets with non-ASCII package cache paths' {
        $fixture = New-Fixture
        $name = [string][char]0x8DEF + [char]0x5F84
        $unicodeCache = Join-Path $fixture.Root $name
        Copy-Item -LiteralPath $fixture.Cache -Destination $unicodeCache -Recurse
        $fixture.Cache = $unicodeCache
        $folders = [ordered]@{}
        $folders[$unicodeCache] = @{}
        $fixture.Assets.packageFolders = $folders
        $packageRoot = Join-Path $unicodeCache 'example.library\1.2.3'
        $licenseRelative = 'legal/' + $name + '.txt'
        $nuspec = [IO.File]::ReadAllText((Join-Path $packageRoot 'example.library.nuspec'))
        Write-FixtureFile $packageRoot 'example.library.nuspec' ($nuspec.Replace('legal/Terms.txt', $licenseRelative))
        Write-FixtureFile $packageRoot $licenseRelative 'Authored Unicode-path terms.'
        Invoke-Collector $fixture
        $destination = Join-Path $fixture.Output ('example.library/1.2.3/' + $licenseRelative)
        Assert-True (Test-Path -LiteralPath $destination -PathType Leaf) 'UTF-8 cache or license path was decoded incorrectly.'
        $package = @((Read-Manifest $fixture).packages | Where-Object { $_.id -eq 'Example.Library' })[0]
        Assert-Equal $package.license.value $licenseRelative 'UTF-8 license path changed in the manifest'
    }
    Invoke-Test 'Rejects a missing package instead of silently omitting notices' {
        $fixture = New-Fixture
        $fixture.Assets.libraries['Absent.Package/1.0.0'] = @{ type = 'package'; path = 'absent.package/1.0.0' }
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects library paths escaping the NuGet root' {
        $fixture = New-Fixture
        $fixture.Assets.libraries['Example.Library/1.2.3'].path = '../outside'
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects declared license paths escaping the package' {
        $fixture = New-Fixture
        $nuspec = 'example.library\1.2.3\example.library.nuspec'
        $text = [IO.File]::ReadAllText((Join-Path $fixture.Cache $nuspec))
        Write-FixtureFile $fixture.Cache $nuspec ($text.Replace('legal/Terms.txt', '../../private.txt'))
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects rooted mixed-separator and alternate-stream license paths' {
        foreach ($unsafe in @('..\..\private.txt', '..\../private.txt', '/private.txt', 'C:\private.txt', '\\server\share\private.txt', 'legal/Terms.txt:secret')) {
            $fixture = New-Fixture
            $nuspec = 'example.library\1.2.3\example.library.nuspec'
            $text = [IO.File]::ReadAllText((Join-Path $fixture.Cache $nuspec))
            Write-FixtureFile $fixture.Cache $nuspec ($text.Replace('legal/Terms.txt', $unsafe))
            Assert-Throws { Invoke-Collector $fixture }
        }
    }
    Invoke-Test 'Rejects a declared license file that is absent' {
        $fixture = New-Fixture
        $nuspec = 'example.library\1.2.3\example.library.nuspec'
        $text = [IO.File]::ReadAllText((Join-Path $fixture.Cache $nuspec))
        Write-FixtureFile $fixture.Cache $nuspec ($text.Replace('legal/Terms.txt', 'legal/Missing.txt'))
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects package metadata that disagrees with the resolved identity' {
        $fixture = New-Fixture
        $nuspec = 'example.library\1.2.3\example.library.nuspec'
        $text = [IO.File]::ReadAllText((Join-Path $fixture.Cache $nuspec))
        Write-FixtureFile $fixture.Cache $nuspec ($text.Replace('<id>Example.Library</id>', '<id>Wrong.Package</id>'))
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects non-exact download dependency versions' {
        $fixture = New-Fixture
        $fixture.Assets.project.frameworks.'net8.0-windows'.downloadDependencies[0].version = '[2.0.0, 3.0.0)'
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects local paths masquerading as license URLs' {
        $fixture = New-Fixture
        $nuspec = 'example.reference\2.0.0\example.reference.nuspec'
        $text = [IO.File]::ReadAllText((Join-Path $fixture.Cache $nuspec))
        Write-FixtureFile $fixture.Cache $nuspec ($text.Replace('https://example.invalid/reference-license', 'file:///C:/Users/private/license.txt'))
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Rejects XML external entities without reading them' {
        $fixture = New-Fixture
        Write-FixtureFile $fixture.Root 'secret.txt' 'https://example.invalid/secret-license'
        $secretUri = (New-Object Uri((Join-Path $fixture.Root 'secret.txt'))).AbsoluteUri
        $xml = @'
<?xml version="1.0"?>
<!DOCTYPE package [<!ENTITY local SYSTEM "{0}">]>
<package><metadata><id>Example.Reference</id><version>2.0.0</version><licenseUrl>&local;</licenseUrl></metadata></package>
'@
        Write-FixtureFile $fixture.Cache 'example.reference\2.0.0\example.reference.nuspec' ($xml -f $secretUri)
        Assert-Throws { Invoke-Collector $fixture }
    }
    Invoke-Test 'Refuses a nonempty destination rather than mixing stale notices' {
        $fixture = New-Fixture
        Write-FixtureFile $fixture.Output 'stale.txt' 'Keep this existing file.'
        Assert-Throws { Invoke-Collector $fixture }
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $fixture.Output 'stale.txt'))) 'Keep this existing file.' 'Existing output was changed'
    }
    Invoke-Test 'Rejects reparse-point notice directories without following them' {
        $fixture = New-Fixture
        $external = Join-Path $fixture.Root 'external'
        Write-FixtureFile $external 'LICENSE.txt' 'Do not traverse this directory.'
        $junction = Join-Path $fixture.Cache 'example.library\1.2.3\linked'
        New-Item -ItemType Junction -Path $junction -Value $external | Out-Null
        try {
            Assert-Throws { Invoke-Collector $fixture }
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Output 'example.library\1.2.3\linked\LICENSE.txt'))) 'Reparse-point contents were redistributed.'
        }
        finally { [IO.Directory]::Delete($junction) }
    }
    Invoke-Test 'Rejects reparse-point output ancestors without changing their targets' {
        $fixture = New-Fixture
        $external = Join-Path $fixture.Root 'external-output'
        Write-FixtureFile $external 'keep.txt' 'Do not change this directory.'
        $junction = Join-Path $fixture.Root 'output-link'
        New-Item -ItemType Junction -Path $junction -Value $external | Out-Null
        $fixture.Output = Join-Path $junction 'nested-notices'
        try {
            Assert-Throws { Invoke-Collector $fixture }
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $external 'nested-notices'))) 'Output escaped through a reparse point.'
        }
        finally { [IO.Directory]::Delete($junction) }
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
        New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'packaging-tests.json'),
            ($results | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    }
    $item = Get-Item -LiteralPath $workspace -Force
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $workspace).Path)
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $resolved)).TrimEnd('\', '/')
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals($parent, $expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($item.Name, $workspaceName, [StringComparison]::Ordinal)) {
        throw 'Refusing to remove an unexpected test workspace.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("Packaging tests: {0} passed, {1} failed." -f ($results.Count - $failed.Count), $failed.Count)
if ($failed.Count -gt 0) { exit 1 }

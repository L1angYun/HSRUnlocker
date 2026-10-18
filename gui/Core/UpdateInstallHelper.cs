using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace StarRailUnlocker.Core
{
    internal static class UpdateInstallHelper
    {
        internal static ProcessStartInfo StartInfo(StagedUpdate staged, int parentId, long parentStartTicks, string error)
        {
            string systemDirectory = Environment.SystemDirectory;
            var start = new ProcessStartInfo(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = systemDirectory,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
                start.ArgumentList.Add(argument);
            start.Environment["HSR_UPD_PARENT_ID"] = parentId.ToString(CultureInfo.InvariantCulture);
            start.Environment["HSR_UPD_PARENT_START"] = parentStartTicks.ToString(CultureInfo.InvariantCulture);
            start.Environment["HSR_UPD_INSTALLER"] = staged.FilePath;
            start.Environment["HSR_UPD_SIZE"] = staged.Package.Size.ToString(CultureInfo.InvariantCulture);
            start.Environment["HSR_UPD_SHA256"] = staged.Package.Sha256;
            start.Environment["HSR_UPD_ERROR"] = error;
            start.Environment["HSR_UPD_LOGS"] = L10n.T("Updates.HelperLogs");
            start.Environment["PSModulePath"] = "";
            start.Environment["PATH"] = systemDirectory + ";" + Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string key in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
                "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS",
                "DOTNET_SHARED_STORE", "COMPLUS_ApplicationMigrationRuntimeActivationConfigPath" })
                start.Environment.Remove(key);
            return start;
        }

        // Fixed code only. Paths, hashes, process identity and translated text travel as environment values.
        internal const string Script = """
            $ErrorActionPreference = 'Stop'
            $PSModuleAutoLoadingPreference = 'None'
            $ready = $false
            $parentExited = $false
            $succeeded = $false
            $cleanup = $false
            $locked = $null
            $parent = $null
            $setup = $null
            $directory = $null
            $installer = $null
            $helperLog = $null
            $installerLog = $null
            $helperLogOwned = $false
            $installerLogOwned = $false
            $installerExitCode = $null
            $failureStage = 'handoff'
            $retainedLogs = @()
            function Assert-PlainAncestors([string] $path) {
                $entry = [System.IO.DirectoryInfo]::new($path)
                while ($null -ne $entry) {
                    if (-not $entry.Exists -or (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                        throw 'Unsafe update staging path.'
                    }
                    $entry = $entry.Parent
                }
            }
            function Assert-Protected([System.Security.AccessControl.FileSystemSecurity] $security) {
                $sid = [System.Security.Principal.SecurityIdentifier]
                if (-not $security.AreAccessRulesProtected -or $security.GetOwner($sid).Value -ne 'S-1-5-32-544') {
                    throw 'Unsafe update staging owner.'
                }
                $rules = $security.GetAccessRules($true, $true, $sid)
                $admin = $false
                $system = $false
                foreach ($rule in $rules) {
                    if ($rule.IsInherited -or $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
                        $rule.FileSystemRights -ne [System.Security.AccessControl.FileSystemRights]::FullControl) {
                        throw 'Unsafe update staging access.'
                    }
                    if ($rule.IdentityReference.Value -eq 'S-1-5-32-544') { $admin = $true }
                    elseif ($rule.IdentityReference.Value -eq 'S-1-5-18') { $system = $true }
                    else { throw 'Unexpected update staging identity.' }
                }
                if (-not $admin -or -not $system -or $rules.Count -ne 2) { throw 'Incomplete update staging access.' }
            }
            function Assert-Hash([System.IO.FileStream] $stream, [long] $size, [string] $expected) {
                if ($stream.Length -ne $size) { throw 'Installer size mismatch.' }
                $stream.Position = 0
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
                    if ($actual -cne $expected.ToLowerInvariant()) { throw 'Installer hash mismatch.' }
                } finally { $sha.Dispose() }
            }
            function Assert-LogStage([string] $directory) {
                Assert-PlainAncestors $directory
                $stage = [System.IO.DirectoryInfo]::new($directory)
                $sections = [System.Security.AccessControl.AccessControlSections]::Owner -bor [System.Security.AccessControl.AccessControlSections]::Access
                Assert-Protected ($stage.GetAccessControl($sections))
            }
            function Open-UpdateLog([string] $directory, [ValidateSet('helper.log', 'installer.log')] [string] $name) {
                Assert-PlainAncestors $directory
                return [System.IO.File]::Open([System.IO.Path]::Combine($directory, $name),
                    [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            }
            function Write-UpdateFailure([System.IO.FileStream] $stream,
                [ValidateSet('validate-stage', 'helper-log', 'installer-log', 'verify-installer', 'start-installer', 'wait-installer', 'installer-exit')]
                [string] $stage, [System.Exception] $failure, $exitCode) {
                $culture = [System.Globalization.CultureInfo]::InvariantCulture
                $exception = $failure.GetBaseException()
                $code = 'none'
                if ($null -ne $exitCode) { $code = ([int] $exitCode).ToString($culture) }
                $record = 'utc=' + [System.DateTime]::UtcNow.ToString('O', $culture) + "`r`n" +
                    'stage=' + $stage + "`r`n" + 'exception=' + $exception.GetType().FullName + "`r`n" +
                    'hresult=' + $exception.HResult.ToString($culture) + "`r`n" + 'installer_exit_code=' + $code + "`r`n"
                $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($record)
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            function Get-InstallerArguments([string] $logPath) {
                if ($logPath.IndexOf('"') -ge 0) { throw 'Invalid update log path.' }
                return '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /HSRUPDATE=1 /LOG="' + $logPath + '"'
            }
            function Complete-UpdateCleanup([string] $directory, [string] $installerName, [bool] $succeeded,
                [bool] $helperOwned, [bool] $installerOwned) {
                try {
                    Assert-PlainAncestors $directory
                    if ([System.IO.Path]::GetFileName($installerName) -cne $installerName) { return }
                } catch { return }
                foreach ($name in @($installerName, 'download.bin')) {
                    try {
                        $file = [System.IO.FileInfo]::new([System.IO.Path]::Combine($directory, $name))
                        if ($file.Exists -and ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
                            [System.IO.File]::Delete($file.FullName)
                        }
                    } catch {}
                }
                foreach ($name in @('helper.log', 'installer.log')) {
                    if (($name -eq 'helper.log' -and -not $helperOwned) -or ($name -eq 'installer.log' -and -not $installerOwned)) { continue }
                    try {
                        $file = [System.IO.FileInfo]::new([System.IO.Path]::Combine($directory, $name))
                        if ($file.Exists -and ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and
                            ($succeeded -or $file.Length -eq 0)) { [System.IO.File]::Delete($file.FullName) }
                    } catch {}
                }
                try { [System.IO.Directory]::Delete($directory) } catch {}
                if (-not $succeeded) {
                    foreach ($name in @('helper.log', 'installer.log')) {
                        if (($name -eq 'helper.log' -and -not $helperOwned) -or ($name -eq 'installer.log' -and -not $installerOwned)) { continue }
                        try {
                            $file = [System.IO.FileInfo]::new([System.IO.Path]::Combine($directory, $name))
                            if ($file.Exists -and ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and $file.Length -gt 0) {
                                $file.FullName
                            }
                        } catch {}
                    }
                }
            }
            try {
                $culture = [System.Globalization.CultureInfo]::InvariantCulture
                $numeric = [System.Globalization.NumberStyles]::None
                $parentId = [int]::Parse($env:HSR_UPD_PARENT_ID, $numeric, $culture)
                $parentTicks = [long]::Parse($env:HSR_UPD_PARENT_START, $numeric, $culture)
                $size = [long]::Parse($env:HSR_UPD_SIZE, $numeric, $culture)
                $expectedHash = $env:HSR_UPD_SHA256
                if ($parentId -le 0 -or $parentTicks -le 0 -or $size -lt 1 -or $size -gt 536870912 -or
                    $expectedHash -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'Invalid update handoff values.' }
                $installer = [System.IO.Path]::GetFullPath($env:HSR_UPD_INSTALLER)
                $directory = [System.IO.Path]::GetDirectoryName($installer)
                $stage = [System.IO.DirectoryInfo]::new($directory)
                $systemRoot = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::CommonApplicationData)
                if (-not $stage.Parent.FullName.Equals($systemRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $stage.Name -cnotmatch '\AHSRUnlocker-Update-[0-9a-f]{32}\z' -or
                    [System.IO.Path]::GetFileName($installer) -cnotmatch '\AHSRUnlocker-(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?-win-x64-Setup\.exe\z') {
                    throw 'Invalid update staging identity.'
                }
                Assert-PlainAncestors $directory
                $sections = [System.Security.AccessControl.AccessControlSections]::Owner -bor [System.Security.AccessControl.AccessControlSections]::Access
                Assert-Protected ($stage.GetAccessControl($sections))
                $file = [System.IO.FileInfo]::new($installer)
                if (-not $file.Exists -or (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw 'Unsafe installer path.'
                }
                Assert-Protected ($file.GetAccessControl($sections))
                $cleanup = $true
                $locked = [System.IO.File]::Open($installer, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                Assert-Hash $locked $size $expectedHash
                $parent = [System.Diagnostics.Process]::GetProcessById($parentId)
                $null = $parent.Handle
                if ($parent.StartTime.ToUniversalTime().Ticks -ne $parentTicks -or $parent.HasExited) {
                    throw 'The update parent process identity changed.'
                }
                [System.Console]::Out.WriteLine('READY')
                [System.Console]::Out.Flush()
                $ready = $true
                if (-not $parent.WaitForExit(300000)) { throw 'The application did not exit before the update deadline.' }
                $parentExited = $true
                $failureStage = 'validate-stage'
                Assert-LogStage $directory
                $failureStage = 'helper-log'
                $helperLog = Open-UpdateLog $directory 'helper.log'
                $helperLogOwned = $true
                $failureStage = 'installer-log'
                $installerLog = Open-UpdateLog $directory 'installer.log'
                $installerLogOwned = $true
                $installerLog.Dispose()
                $installerLog = $null
                $failureStage = 'verify-installer'
                Assert-Hash $locked $size $expectedHash
                $info = [System.Diagnostics.ProcessStartInfo]::new()
                $info.FileName = $installer
                $info.Arguments = Get-InstallerArguments ([System.IO.Path]::Combine($directory, 'installer.log'))
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
                $info.WorkingDirectory = [System.Environment]::SystemDirectory
                $failureStage = 'start-installer'
                $setup = [System.Diagnostics.Process]::Start($info)
                if ($null -eq $setup) { throw 'The verified update installer did not start.' }
                $failureStage = 'wait-installer'
                $setup.WaitForExit()
                $installerExitCode = $setup.ExitCode
                $failureStage = 'installer-exit'
                if ($installerExitCode -ne 0 -and $installerExitCode -ne 3010) { throw 'The update installer did not finish successfully.' }
                $succeeded = $true
            } catch {
                if ($parentExited -and $null -ne $helperLog) {
                    try { Write-UpdateFailure $helperLog $failureStage $_.Exception $installerExitCode } catch {}
                }
                if (-not $ready) {
                    [System.Console]::Out.WriteLine('ERROR')
                    [System.Console]::Out.Flush()
                }
            } finally {
                if ($null -ne $helperLog) {
                    try { $helperLog.Flush($true) } catch {}
                    try { $helperLog.Dispose() } catch {}
                }
                if ($null -ne $installerLog) { try { $installerLog.Dispose() } catch {} }
                if ($null -ne $locked) { try { $locked.Dispose() } catch {} }
                if ($null -ne $parent) { try { $parent.Dispose() } catch {} }
                if ($null -ne $setup) { try { $setup.Dispose() } catch {} }
                if ($cleanup) {
                    $retainedLogs = @(Complete-UpdateCleanup $directory ([System.IO.Path]::GetFileName($installer)) $succeeded $helperLogOwned $installerLogOwned)
                }
            }
            if ($ready -and -not $succeeded) {
                try {
                    $message = $env:HSR_UPD_ERROR
                    if ($retainedLogs.Count -gt 0) {
                        $message += [System.Environment]::NewLine + [System.Environment]::NewLine + $env:HSR_UPD_LOGS +
                            [System.Environment]::NewLine + [string]::Join([System.Environment]::NewLine, [string[]] $retainedLogs)
                    }
                    $null = [System.Reflection.Assembly]::Load('System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
                    $null = [System.Windows.Forms.MessageBox]::Show($message, 'HSRUnlocker',
                        [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
                } catch {}
            }
            """;
    }
}

using System.Diagnostics;
using System.Text;
using StarRailUnlocker.Core;

internal static class UpdateHelperVerification
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "HSRUnlocker-helper-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var staged = new StagedUpdate(new UpdatePackage("1.2.3", "HSRUnlocker-1.2.3-win-x64-Setup.exe",
                new Uri("https://github.com/L1angYun/HSRUnlocker/releases/download/v1.2.3/HSRUnlocker-1.2.3-win-x64-Setup.exe"),
                1, new string('0', 64)), Path.Combine(root, "uncreated launch fixture"), false, null);
            ProcessStartInfo handoff = UpdateInstallHelper.StartInfo(staged, 12345, 67890, "fixture failure");
            // Count even unnecessary argument quotes and the final NUL, leaving room below CreateProcess's limit.
            int commandLength = handoff.FileName.Length + 3 + handoff.ArgumentList.Sum(argument => argument.Length + 3);
            check(commandLength < 32000, $"actual encoded helper command line stays below the conservative Windows launch limit ({commandLength} characters)");
            string originalLanguage = L10n.Preference;
            try
            {
                foreach (string language in new[] { "zh-CN", "en-US", "ja-JP", "ko-KR" })
                {
                    L10n.SetLanguage(language);
                    handoff = UpdateInstallHelper.StartInfo(staged, 12345, 67890, "fixture failure");
                    string notice = handoff.Environment["HSR_UPD_LOGS"];
                    check(!string.IsNullOrWhiteSpace(notice) && notice != "Updates.HelperLogs"
                        && notice == L10n.T("Updates.HelperLogs") && !UpdateInstallHelper.Script.Contains(notice, StringComparison.Ordinal),
                        $"retained-log notice uses environment-only localized text in {language}");
                }
            }
            finally { L10n.SetLanguage(originalLanguage); }
            var result = RunFixture(UpdateInstallHelper.Script, Path.Combine(root, "green"));
            check(result.Exited, "helper function fixture exits within its bounded hidden child lifetime");
            foreach (string line in result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("PASS: ", StringComparison.Ordinal)) check(true, line[6..]);
                else if (line.StartsWith("FAIL: ", StringComparison.Ordinal)) check(false, line[6..]);
                else throw new InvalidOperationException("Unexpected helper fixture output: " + line);
            }
            check(result.ExitCode == 0 && result.Errors.Length == 0, "helper fixture completes without errors: " + result.Errors);

            // Mutate only the fixture's source copy, then require a specific executed behavioral failure.
            string noLog = UpdateInstallHelper.Script.Replace(
                "return '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /HSRUPDATE=1 /LOG=\"' + $logPath + '\"'",
                "return '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /HSRUPDATE=1'", StringComparison.Ordinal);
            result = RunFixture(noLog, Path.Combine(root, "no-log-control"));
            check(result.Exited && result.ExitCode == 1 && result.Errors.Length == 0 && result.Output.Contains(
                "FAIL: Inno arguments preserve the existing switches and quote a fixed reserved log path containing spaces", StringComparison.Ordinal),
                "executed argument negative control rejects the old installer command without /LOG");
            string deleteLogs = UpdateInstallHelper.Script.Replace("($succeeded -or $file.Length -eq 0)", "($true)", StringComparison.Ordinal);
            result = RunFixture(deleteLogs, Path.Combine(root, "no-retention-control"));
            check(result.Exited && result.ExitCode == 1 && result.Errors.Length == 0 && result.Output.Contains(
                "FAIL: failure retains only useful owned logs and removes payloads: helper", StringComparison.Ordinal),
                "real-file cleanup negative control rejects deleting useful logs after failure");
        }
        finally
        {
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.GetDirectoryName(Path.GetFullPath(root)).Equals(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("HSRUnlocker-helper-fixtures-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected helper fixture cleanup path.");
            foreach (string fixture in new[] { "green", "no-log-control", "no-retention-control" })
            {
                string junction = Path.Combine(root, fixture, "stage-junction");
                if (Directory.Exists(junction)) Directory.Delete(junction);
            }
            Directory.Delete(root, true);
        }
    }

    private static (bool Exited, int ExitCode, string Output, string Errors) RunFixture(string source, string root)
    {
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("& ([scriptblock]::Create($env:HSR_HELPER_FIXTURE))")) })
            start.ArgumentList.Add(argument);
        start.Environment["HSR_HELPER_FIXTURE"] = Fixture;
        start.Environment["HSR_HELPER_SOURCE"] = source;
        start.Environment["HSR_HELPER_ROOT"] = root;
        start.Environment["PSModulePath"] = "";
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start helper function fixture.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        bool exited = child.WaitForExit(30000);
        if (!exited)
        {
            child.Kill();
            child.WaitForExit(5000);
        }
        return (exited, child.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // Only named function definitions are evaluated. The production handoff is never invoked.
    private const string Fixture = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        function Check([bool] $value, [string] $name) {
            if (-not $value) { [Console]::Out.WriteLine('FAIL: ' + $name); exit 1 }
            [Console]::Out.WriteLine('PASS: ' + $name)
        }
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($env:HSR_HELPER_SOURCE, [ref] $tokens, [ref] $errors)
        Check ($errors.Count -eq 0) 'production helper parses without errors'
        $names = @('Assert-PlainAncestors', 'Assert-Protected', 'Assert-LogStage', 'Open-UpdateLog',
            'Write-UpdateFailure', 'Get-InstallerArguments', 'Complete-UpdateCleanup')
        foreach ($name in $names) {
            $definitions = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
            }, $false))
            Check ($definitions.Count -eq 1) ('production defines safe function ' + $name)
            . ([scriptblock]::Create($definitions[0].Extent.Text))
        }
        $root = $env:HSR_HELPER_ROOT
        $installerName = 'HSRUnlocker-1.2.3-win-x64-Setup.exe'
        function New-Stage([string] $name) {
            $path = [IO.Path]::Combine($root, $name)
            $null = [IO.Directory]::CreateDirectory($path)
            [IO.File]::WriteAllText([IO.Path]::Combine($path, $installerName), 'not an executable')
            [IO.File]::WriteAllText([IO.Path]::Combine($path, 'download.bin'), 'partial')
            return $path
        }
        function Open-Logs([string] $path) {
            $helper = Open-UpdateLog $path 'helper.log'
            $helper.Dispose()
            $setup = Open-UpdateLog $path 'installer.log'
            $setup.Dispose()
        }
        $stage = New-Stage 'success with spaces'
        Open-Logs $stage
        [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'helper.log'), 'helper diagnostic')
        [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'installer.log'), 'synthetic setup diagnostic')
        $retained = @(Complete-UpdateCleanup $stage $installerName $true $true $true)
        Check ($retained.Count -eq 0 -and -not [IO.Directory]::Exists($stage)) 'success removes all owned payloads, logs and empty staging directory'
        foreach ($mode in @('helper', 'installer', 'both', 'empty')) {
            $stage = New-Stage ('failure-' + $mode)
            Open-Logs $stage
            if ($mode -eq 'helper' -or $mode -eq 'both') {
                [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'helper.log'), 'helper diagnostic')
            }
            if ($mode -eq 'installer' -or $mode -eq 'both') {
                [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'installer.log'), 'synthetic setup diagnostic')
            }
            $retained = @(Complete-UpdateCleanup $stage $installerName $false $true $true)
            $expected = 0
            if ($mode -eq 'helper' -or $mode -eq 'installer') { $expected = 1 }
            if ($mode -eq 'both') { $expected = 2 }
            Check ($retained.Count -eq $expected -and -not [IO.File]::Exists([IO.Path]::Combine($stage, $installerName)) -and
                -not [IO.File]::Exists([IO.Path]::Combine($stage, 'download.bin'))) ('failure retains only useful owned logs and removes payloads: ' + $mode)
            foreach ($path in $retained) {
                $file = [IO.FileInfo]::new($path)
                Check ($file.Exists -and $file.Length -gt 0 -and ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'every advertised log is surviving regular nonempty evidence'
            }
            if ($mode -eq 'empty') {
                Check (-not [IO.Directory]::Exists($stage)) 'empty owned log placeholders are removed on failure'
            }
        }
        $stage = New-Stage 'no-logs'
        $retained = @(Complete-UpdateCleanup $stage $installerName $false $false $false)
        Check ($retained.Count -eq 0 -and -not [IO.Directory]::Exists($stage)) 'failure without owned diagnostics advertises nothing and removes payloads'
        $stage = New-Stage 'sanitized'
        $stream = Open-UpdateLog $stage 'helper.log'
        $failure = [InvalidOperationException]::new('SECRET-token https://downloads.invalid/setup.exe?sig=PRIVATE')
        Write-UpdateFailure $stream 'installer-exit' $failure 1603
        $stream.Dispose()
        $retained = @(Complete-UpdateCleanup $stage $installerName $false $true $false)
        $text = [IO.File]::ReadAllText([IO.Path]::Combine($stage, 'helper.log'))
        Check ($retained.Count -eq 1 -and $text.Contains('stage=installer-exit') -and
            $text.Contains('exception=System.InvalidOperationException') -and $text.Contains('hresult=-2146233079') -and
            $text.Contains('installer_exit_code=1603') -and $text -match '(?m)^utc=\d{4}-\d\d-\d\dT' -and
            -not $text.Contains('SECRET') -and -not $text.Contains('PRIVATE') -and -not $text.Contains('https://')) 'failure log contains safe time, stage, exception identity, HResult and setup exit code without message secrets'
        $stage = New-Stage 'no-exit-code'
        $stream = Open-UpdateLog $stage 'helper.log'
        Write-UpdateFailure $stream 'start-installer' ([IO.IOException]::new('not logged')) $null
        $stream.Dispose()
        $text = [IO.File]::ReadAllText([IO.Path]::Combine($stage, 'helper.log'))
        Check ($text.Contains('installer_exit_code=none')) 'failure before setup starts records no invented installer exit code'
        foreach ($name in @('helper.log', 'installer.log')) {
            foreach ($kind in @('file', 'directory')) {
                $stage = New-Stage ('collision-' + $name + '-' + $kind)
                $path = [IO.Path]::Combine($stage, $name)
                if ($kind -eq 'file') { [IO.File]::WriteAllText($path, 'collision sentinel') }
                else { $null = [IO.Directory]::CreateDirectory($path) }
                $refused = $false
                try { $stream = Open-UpdateLog $stage $name; $stream.Dispose() } catch { $refused = $true }
                $retained = @(Complete-UpdateCleanup $stage $installerName $false $false $false)
                $preserved = [IO.Directory]::Exists($path)
                if ($kind -eq 'file') { $preserved = [IO.File]::ReadAllText($path) -ceq 'collision sentinel' }
                Check ($refused -and $preserved -and $retained.Count -eq 0) ('CreateNew refuses and preserves unowned log collision: ' + $name + ' ' + $kind)
                $retained = @(Complete-UpdateCleanup $stage $installerName $true $false $false)
                $preserved = [IO.Directory]::Exists($path)
                if ($kind -eq 'file') { $preserved = [IO.File]::ReadAllText($path) -ceq 'collision sentinel' }
                Check ($preserved -and $retained.Count -eq 0) ('success cleanup also preserves unowned log collision: ' + $name + ' ' + $kind)
            }
        }
        $stage = New-Stage 'installer-collision-with-helper'
        $stream = Open-UpdateLog $stage 'helper.log'
        [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'installer.log'), 'unowned setup sentinel')
        $refused = $false
        try { $reservation = Open-UpdateLog $stage 'installer.log'; $reservation.Dispose() }
        catch {
            $refused = $true
            Write-UpdateFailure $stream 'installer-log' $_.Exception $null
        }
        $stream.Dispose()
        $retained = @(Complete-UpdateCleanup $stage $installerName $false $true $false)
        Check ($refused -and $retained.Count -eq 1 -and $retained[0] -ceq [IO.Path]::Combine($stage, 'helper.log') -and
            [IO.File]::ReadAllText([IO.Path]::Combine($stage, 'installer.log')) -ceq 'unowned setup sentinel') 'Inno reservation collision retains only the owned helper failure log and preserves the collided setup log'
        $stage = New-Stage 'unknown'
        [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'unknown.txt'), 'unknown sentinel')
        $unknown = [IO.Path]::Combine($stage, 'unknown-subdir')
        $null = [IO.Directory]::CreateDirectory($unknown)
        [IO.File]::WriteAllText([IO.Path]::Combine($unknown, 'keep.txt'), 'nested sentinel')
        $retained = @(Complete-UpdateCleanup $stage $installerName $true $false $false)
        Check ([IO.File]::ReadAllText([IO.Path]::Combine($stage, 'unknown.txt')) -ceq 'unknown sentinel' -and
            [IO.File]::ReadAllText([IO.Path]::Combine($unknown, 'keep.txt')) -ceq 'nested sentinel') 'cleanup preserves unknown files and subdirectories without recursive deletion'
        $stage = New-Stage 'locked'
        Open-Logs $stage
        [IO.File]::WriteAllText([IO.Path]::Combine($stage, 'helper.log'), 'durable diagnostic')
        $held = [IO.File]::Open([IO.Path]::Combine($stage, $installerName), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $retained = @(Complete-UpdateCleanup $stage $installerName $false $true $true) }
        finally { $held.Dispose() }
        Check ($retained.Count -eq 1 -and [IO.File]::Exists([IO.Path]::Combine($stage, $installerName)) -and
            -not [IO.File]::Exists([IO.Path]::Combine($stage, 'download.bin')) -and
            -not [IO.File]::Exists([IO.Path]::Combine($stage, 'installer.log'))) 'a locked payload does not block independent partial-download and empty-log cleanup'
        $stage = New-Stage 'log-directory'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::Combine($stage, 'helper.log'))
        $retained = @(Complete-UpdateCleanup $stage $installerName $false $true $false)
        Check ($retained.Count -eq 0 -and [IO.Directory]::Exists([IO.Path]::Combine($stage, 'helper.log'))) 'same-name directory is neither removed nor advertised even with a stale ownership flag'
        $path = [IO.Path]::Combine($root, 'spaces in stage', 'installer.log')
        $arguments = Get-InstallerArguments $path
        Check ($arguments -ceq ('/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /HSRUPDATE=1 /LOG="' + $path + '"')) 'Inno arguments preserve the existing switches and quote a fixed reserved log path containing spaces'
        $stage = New-Stage 'unsafe-acl'
        $refused = $false
        try { Assert-LogStage $stage } catch { $refused = $true }
        Check $refused 'diagnostic stage gate rejects a normal unprotected inherited temporary-directory ACL'
        $outside = New-Stage 'outside-sentinel'
        [IO.File]::WriteAllText([IO.Path]::Combine($outside, 'helper.log'), 'outside log sentinel')
        $link = [IO.Path]::Combine($root, 'stage-junction')
        Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1'))
        $null = New-Item -ItemType Junction -Path $link -Target $outside
        try {
            $refused = $false
            try { $stream = Open-UpdateLog $link 'installer.log'; $stream.Dispose() } catch { $refused = $true }
            $retained = @(Complete-UpdateCleanup $link $installerName $true $true $true)
            Check ($refused -and $retained.Count -eq 0 -and
                [IO.File]::ReadAllText([IO.Path]::Combine($outside, 'helper.log')) -ceq 'outside log sentinel' -and
                [IO.File]::ReadAllText([IO.Path]::Combine($outside, $installerName)) -ceq 'not an executable') 'reparse staging refuses log creation and cleanup without touching the outside sentinel'
        } finally { [IO.Directory]::Delete($link) }
        $source = $env:HSR_HELPER_SOURCE
        $wait = $source.IndexOf('$parent.WaitForExit(300000)')
        $exited = $source.IndexOf('$parentExited = $true', $wait)
        $gate = $source.IndexOf('Assert-LogStage $directory', $exited)
        $helper = $source.IndexOf('Open-UpdateLog $directory ''helper.log''', $gate)
        $setupLog = $source.IndexOf('Open-UpdateLog $directory ''installer.log''', $helper)
        $close = $source.IndexOf('$installerLog.Dispose()', $setupLog)
        $arguments = $source.IndexOf('Get-InstallerArguments', $close)
        Check ($wait -ge 0 -and $exited -gt $wait -and $gate -gt $exited -and $helper -gt $gate -and
            $setupLog -gt $helper -and $close -gt $setupLog -and $arguments -gt $close) 'live helper gates reservations after authenticated parent exit and stage revalidation, then closes Inno reservation before building arguments'
        $cleanup = $source.LastIndexOf('Complete-UpdateCleanup')
        $notify = $source.IndexOf('[System.Windows.Forms.MessageBox]::Show')
        Check ($notify -gt $cleanup -and $cleanup -gt $source.IndexOf('$helperLog.Dispose()')) 'live helper flushes and disposes diagnostics and attempts cleanup before notification'
        """;
}

using System.Text.Json;
using StarRailUnlocker.Core;

internal static class Program
{
    private static readonly List<string> Checks = new();

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + description);
        Checks.Add(description);
        Console.WriteLine("PASS: " + description);
    }

    private static int Main(string[] args)
    {
        if (ExecutableEntryVerification.TryRunChild(args, out int childResult)) return childResult;
        if (args.SequenceEqual(new[] { "--help" }))
        {
            Console.WriteLine("Run without arguments for Windows x64 own-process, synthetic PE, mobile-fixture and reversible native-entry child-fixture tests. No installed game or live settings are accessed.");
            return 0;
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Unsupported arguments. This portable suite has no live, launch, disk-client or automatic process-selection mode.");
            return 2;
        }
        try
        {
            Check(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "portable suite runs in an x64 Windows test process");
            PreferencesVerification.Run(Check);
            LocalizationVerification.Run(Check);
            UpdateVerification.Run(Check);
            UpdateInstallVerification.Run(Check);
            InstallerGuardVerification.Run(Check);
            // Keep existing diagnostic assertions independent of the Windows display language.
            L10n.SetLanguage("en-US");
            MobileLaunchVerification.Run(Check, Array.Empty<string>());
            MobileEntryVerification.Run(Check);
            MobileUnwindVerification.Run(Check);
            MobilePeVerification.Run(Check);
            VerifyLaunchGuard();
            SyntheticPeVerification.Run(Check);
            CameraBodyVerification.Run(Check);
            CameraHeaderVerification.Run(Check);
            NativeWrapperVerification.Run(Check);
            AntiFadeWrapperVerification.Run(Check);
            GatewayUnwindVerification.Run(Check);
            ExecutableEntryVerification.Run(Check);
            LifecycleVerification.Run(Check);
            WriteReport(null);
            Console.WriteLine($"PORTABLE VERIFICATION: {Checks.Count} checks passed; only synthetic files, own allocations and one bounded fixture child were used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            WriteReport(ex);
            return 1;
        }
    }

    private static void WriteReport(Exception error)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "portable-verification.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow,
                passed = Checks,
                success = error == null,
                error = error == null ? null : new { type = error.GetType().FullName, message = error.Message },
                scope = "Synthetic files, fake graphics reads, own allocations and one bounded same-assembly fixture child; no installed game files, live settings, network or game launch."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not write test report: " + ex.Message); }
    }

    private static void VerifyLaunchGuard()
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HSRUnlocker-guard-" + Guid.NewGuid().ToString("N")));
        string executable = Path.Combine(directory, "StarRail.exe");
        string compatibility = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(executable, Array.Empty<byte>());
        try
        {
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", "RunAsInvoker");
            bool refused = false;
            try { GameLauncher.Launch(new UnlockConfig { ProcessPath = executable }); }
            catch (InvalidOperationException ex) { refused = ex.Message.Contains("RunAsInvoker", StringComparison.OrdinalIgnoreCase); }
            Check(refused && new FileInfo(executable).Length == 0,
                "RunAsInvoker guard refuses an empty own StarRail.exe fixture before Process.Start");
        }
        finally
        {
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", compatibility);
            if (!Path.GetFullPath(executable).Equals(Path.Combine(directory, "StarRail.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected launch-guard cleanup path");
            File.Delete(executable);
            Directory.Delete(directory);
        }
        Check(Environment.GetEnvironmentVariable("__COMPAT_LAYER") == compatibility, "launch guard restores the process environment after its fixture check");
    }

}

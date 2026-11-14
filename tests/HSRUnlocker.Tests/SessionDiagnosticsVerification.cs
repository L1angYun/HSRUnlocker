using StarRailUnlocker.Core;

internal static class SessionDiagnosticsVerification
{
    public static void Run(Action<bool, string> check)
    {
        var diagnostics = new SessionDiagnostics();
        check(!diagnostics.HasEntries && diagnostics.CreateReport("1.3.7") == "",
            "empty diagnostics does not export a misleading report");

        diagnostics.Add(DiagnosticStep.WaitForGame, DiagnosticResult.Started);
        diagnostics.AddTransition(DiagnosticStep.UpdateFrameCap, DiagnosticResult.Reapplied, 60, 320);
        diagnostics.AddTransfer(DiagnosticStep.MonitorFrameCap, new Int32Transfer(false, 2, 299));
        diagnostics.AddValues(DiagnosticStep.RestoreReadback, DiagnosticResult.Mismatch, 60, 120);
        diagnostics.Add(DiagnosticStep.RestoreFrameCap, DiagnosticResult.Confirmed);
        string snapshot = diagnostics.CreateReport("1.3.7");
        check(diagnostics.HasEntries && snapshot.Contains("HSRUnlocker: 1.3.7")
            && snapshot.Contains("Windows: ") && snapshot.Contains("WaitForGame Started")
            && snapshot.Contains("UpdateFrameCap Reapplied previous=60 requested=320")
            && snapshot.Contains("MonitorFrameCap TransferFailed win32=299 bytes=2/4")
            && snapshot.Contains("RestoreReadback Mismatch expected=60 observed=120")
            && snapshot.Contains("RestoreFrameCap Confirmed"),
            "diagnostics includes structured version, OS build, phases, Win32 code, byte count and readback values");
        check(snapshot.Contains("Z ") && !snapshot.Contains("0x")
            && diagnostics.CreateReport("C:\\Users\\PrivateFixture").Contains("HSRUnlocker: unknown"),
            "diagnostics uses UTC dates, omits addresses and cannot print raw version text");

        diagnostics.Reset();
        check(!diagnostics.HasEntries && diagnostics.CreateReport("1.3.7") == ""
            && snapshot.Contains("win32=299"), "reset clears the live report without changing an already-created snapshot");
        diagnostics.AddTransfer(DiagnosticStep.RestoreFrameCap, new Int32Transfer(true, 2, 0));
        check(diagnostics.CreateReport("1.3.7").Contains("bytes=2/4")
            && !diagnostics.CreateReport("1.3.7").Contains("win32=0"), "unavailable Win32 code is omitted");

        diagnostics.Reset();
        diagnostics.Add(DiagnosticStep.WaitForGame, DiagnosticResult.Started);
        diagnostics.AddTransfer(DiagnosticStep.MonitorFrameCap, new Int32Transfer(false, 0, 5));
        Parallel.For(0, 1200, i => diagnostics.AddValues(DiagnosticStep.MonitorFrameCap, DiagnosticResult.Applied, i, i + 1));
        var lines = diagnostics.CreateReport("1.3.7").Split('\n');
        check(lines.Length < 200 && lines.Any(line => line.Contains("WaitForGame Started"))
            && lines.Any(line => line.Contains("win32=5"))
            && lines.Count(line => line.Contains("MonitorFrameCap Applied")) > 1,
            "structured diagnostics stays bounded and preserves the first event and failure under concurrent writes");
    }
}

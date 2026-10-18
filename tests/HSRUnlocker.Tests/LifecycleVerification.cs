using System.Reflection;
using StarRailUnlocker.Core;

internal static class LifecycleVerification
{
    public static void Run(Action<bool, string> check)
    {
        VerifyLaunchSettingsSnapshot(check);
        VerifyFovConflictRemainsPending(check);
        VerifySessionRetainsFailedCleanup(check);
    }

    private static void VerifyLaunchSettingsSnapshot(Action<bool, string> check)
    {
        var config = new UnlockConfig
        {
            ProcessPath = @"C:\owned-fixture\StarRail.exe",
            UseMobileUI = true,
            GraphicsSource = MobileGraphicsSource.Global,
            SafeAreaMode = MobileSafeAreaMode.Custom,
            SafeAreaHorizontalPercent = 5,
            TargetFramerate = 120
        };
        using var session = new UnlockSession(config);
        using var waiting = new ManualResetEventSlim();
        session.Changed += (state, _) => { if (state == SessionState.Patching) waiting.Set(); };
        try
        {
            // An explicit own PID skips game discovery; no Unity module is loaded in this test process.
            session.Start(checked((uint)Environment.ProcessId));
            if (!waiting.Wait(3000)) throw new TimeoutException("Own session did not reach its module wait.");
            config.ProcessPath = @"C:\different-fixture\StarRail.exe";
            config.UseMobileUI = false;
            config.GraphicsSource = MobileGraphicsSource.China;
            config.SafeAreaMode = MobileSafeAreaMode.Original;
            config.SafeAreaHorizontalPercent = 15;
            config.TargetFramerate = 240;
            var snapshot = typeof(UnlockSession).GetField("_launchConfig", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(session) as UnlockConfig;
            check(snapshot is not null && snapshot.ProcessPath == @"C:\owned-fixture\StarRail.exe"
                && snapshot.UseMobileUI && snapshot.GraphicsSource == MobileGraphicsSource.Global
                && snapshot.SafeAreaMode == MobileSafeAreaMode.Custom && snapshot.SafeAreaHorizontalPercent == 5,
                "session freezes mobile launch and safe-area settings before its worker can observe next-launch edits");
            check(ReferenceEquals(session.Config, config) && session.Config.TargetFramerate == 240,
                "launch-only snapshot leaves the session's shared live FPS configuration intact");
        }
        finally
        {
            session.Stop();
            var worker = GetField(session, "_worker") as Task;
            if (worker is not null && !worker.Wait(5000))
                throw new TimeoutException("Own launch-snapshot session did not settle.");
        }
        check(!session.IsRunning, "own launch-snapshot worker stops without scanning for or launching a game");
    }

    private static void VerifyFovConflictRemainsPending(Action<bool, string> check)
    {
        using var process = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong region = process.Allocate(0x2000);
        if (region == 0) throw new InvalidOperationException("Cannot allocate own FOV lifecycle fixture.");
        ulong slot = region + 0x80, original = region + 0x1800, foreign = region + 0x1900;
        var build = BuildWithSlot(slot);
        var patch = new FovPatcher(process, build);
        SetField(patch, "_region", region);
        SetField(patch, "_original", original);
        SetField(patch, "_published", true);
        SetProperty(patch, nameof(FovPatcher.IsInstalled), true);
        try
        {
            if (!process.WriteData(slot, BitConverter.GetBytes(foreign)))
                throw new InvalidOperationException("Cannot prepare own foreign-slot lifecycle fixture.");
            check(!patch.Remove() && process.ReadUInt64(slot) == foreign,
                "FOV removal refuses a competing own-slot pointer and preserves foreign ownership");
            check(!patch.Remove() && !string.IsNullOrEmpty(patch.LastError)
                && process.ReadUInt64(slot) == foreign,
                "repeated FOV removal keeps unresolved foreign ownership visible instead of claiming restoration");
            if (!process.WriteData(slot, BitConverter.GetBytes(original)))
                throw new InvalidOperationException("Cannot restore own lifecycle fixture slot.");
            check(patch.Remove() && !patch.IsInstalled && string.IsNullOrEmpty(patch.LastError),
                "FOV removal clears its pending error once the exact original pointer is observed");
        }
        finally
        {
            process.WriteData(slot, BitConverter.GetBytes(original));
            patch.Dispose();
            if (!process.HasPendingPointerExchange)
                check(process.Free(region), "own FOV lifecycle allocation is freed after its pointer helpers settle");
        }
    }

    private static void VerifySessionRetainsFailedCleanup(Action<bool, string> check)
    {
        using var owner = new TargetProcess(checked((uint)Environment.ProcessId));
        var target = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong antiRegion = owner.Allocate(0x2000), fovRegion = owner.Allocate(0x2000);
        if (antiRegion == 0 || fovRegion == 0)
            throw new InvalidOperationException("Cannot allocate own session lifecycle fixture.");
        ulong cap = fovRegion + 0x90, slot = fovRegion + 0x80, original = fovRegion + 0x1800;
        var build = BuildWithSlot(slot);
        var antiFade = new AntiFadePatcher(target, build);
        SetField(antiFade, "_region", antiRegion);
        SetField(antiFade, "_published", true);
        SetField(antiFade, "_enabled", true);
        var fov = new FovPatcher(target, build);
        SetField(fov, "_region", fovRegion);
        SetField(fov, "_original", original);
        SetField(fov, "_published", true);
        var session = new UnlockSession(new UnlockConfig());
        SetField(session, "_proc", target);
        SetField(session, "_build", build);
        SetField(session, "_antiFade", antiFade);
        SetField(session, "_fov", fov);
        SetField(session, "_capWritten", true);
        SetProperty(session, nameof(UnlockSession.CapAddress), cap);
        SetProperty(session, nameof(UnlockSession.OriginalValue), 60);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        SetField(session, "_cts", cancellation);
        using var begin = new ManualResetEventSlim();
        using var pending = new ManualResetEventSlim();
        session.Changed += (state, _) => { if (state == SessionState.Failed) pending.Set(); };
        Task worker = null, disposing = null;
        try
        {
            if (!owner.WriteData(slot, BitConverter.GetBytes(original)) || !owner.WriteInt32(cap, 144)
                || !owner.WriteData(antiRegion, new byte[] { 1 })
                || !Native.VirtualProtectEx(owner.Handle, (IntPtr)(long)antiRegion, (IntPtr)0x1000, 0x01, out _))
                throw new InvalidOperationException("Cannot prepare the own no-access cleanup failure fixture.");
            // A cancelled Run reaches the real teardown before discovery, module lookup or attachment.
            worker = Task.Run(() =>
            {
                begin.Wait();
                Invoke(session, "Run", checked((uint)Environment.ProcessId), false, cancellation.Token);
            });
            SetField(session, "_worker", worker);
            begin.Set();
            if (!pending.Wait(3000)) throw new TimeoutException("Session did not report its controlled restoration failure.");
            check(session.IsRunning && target.Handle != IntPtr.Zero && !string.IsNullOrEmpty(session.AntiFadeError),
                "failed session teardown stays running, reports the failure and retains its target handle");
            check(owner.ReadInt32(cap) == 60 && GetField(session, "_fov") == null
                && ReferenceEquals(GetField(session, "_antiFade"), antiFade),
                "failed anti-fade cleanup retains its owner while FOV and frame-cap restoration complete independently");
            session.Start(checked((uint)Environment.ProcessId));
            check(ReferenceEquals(GetField(session, "_worker"), worker),
                "Start cannot replace a worker whose previous patches still require restoration");
            using var disposeEntered = new ManualResetEventSlim();
            disposing = Task.Run(() => { disposeEntered.Set(); session.Dispose(); });
            disposeEntered.Wait();
            check(!disposing.Wait(100), "Dispose waits for unresolved restoration instead of abandoning its live patch owner");
            if (!Native.VirtualProtectEx(owner.Handle, (IntPtr)(long)antiRegion, (IntPtr)0x1000,
                    Native.PAGE_READWRITE, out _))
                throw new InvalidOperationException("Cannot release the own controlled cleanup failure.");
            if (!disposing.Wait(5000)) throw new TimeoutException("Session did not finish after restoration became possible.");
            check(!session.IsRunning && target.Handle == IntPtr.Zero && session.State == SessionState.Idle
                && string.IsNullOrEmpty(session.AntiFadeError) && owner.Read(antiRegion, 1) is { } disabled && disabled[0] == 0,
                "worker retries retained anti-fade state, disables it and closes the handle only after recovery");
        }
        finally
        {
            begin.Set();
            Native.VirtualProtectEx(owner.Handle, (IntPtr)(long)antiRegion, (IntPtr)0x1000,
                Native.PAGE_READWRITE, out _);
            if (worker != null && !worker.Wait(5000))
                throw new TimeoutException("Own lifecycle worker did not settle; its allocations were retained.");
            if (disposing != null && !disposing.Wait(5000))
                throw new TimeoutException("Own lifecycle Dispose did not settle; its allocations were retained.");
            session.Dispose();
            target.Dispose();
            check(owner.Free(antiRegion) && owner.Free(fovRegion),
                "own session lifecycle allocations are freed only after teardown and Dispose have both settled");
        }
    }

    private static ResolvedGameBuild BuildWithSlot(ulong slot)
    {
        var build = new ResolvedGameBuild();
        SetProperty(build, nameof(ResolvedGameBuild.CameraSetterSlot), slot);
        return build;
    }

    private static void SetProperty(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SetValue(target, value);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    private static void Invoke(object target, string name, params object[] arguments)
    {
        try
        {
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

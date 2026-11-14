using StarRailUnlocker.Core;

internal static class FrameCapIoVerification
{
    public static void Run(Action<bool, string> check)
    {
        var retry = new FrameCapReadFailures();
        var partial = new Int32Transfer(false, 2, 299);
        var shortRead = new Int32Transfer(true, 2, 0);
        var denied = new Int32Transfer(false, 0, 5);
        check(retry.CanRetry(partial) && retry.CanRetry(shortRead) && !retry.CanRetry(partial),
            "only the first two consecutive incomplete monitor reads defer a session failure");
        retry.Reset();
        check(retry.CanRetry(partial), "a verified monitor read resets the consecutive failure count");
        retry.Reset();
        check(!retry.CanRetry(denied), "a denied monitor read fails without deferring teardown");
        check(!partial.Succeeded && !shortRead.Succeeded && !denied.Succeeded,
            "neither a short transfer nor a failed native call is a successful int32 transfer");
        check(partial.DescribeFailure().Contains("299") && shortRead.DescribeFailure().Contains("2/4")
            && denied.DescribeFailure().Contains("5"), "cap I/O diagnostic includes the concrete code and byte counts");

        uint pid = checked((uint)Environment.ProcessId);
        using var owner = new TargetProcess(pid);
        using var reader = new TargetProcess(pid, readOnly: true);
        ulong address = owner.Allocate(4096);
        if (address == 0) throw new InvalidOperationException("Cannot allocate own cap I/O fixture.");
        try
        {
            check(owner.TryWriteInt32(address, int.MinValue).Succeeded
                && reader.TryReadInt32(address, out int observed).Succeeded && observed == int.MinValue,
                "native int32 read separates a successful value from the legacy failure sentinel");
            Int32Transfer write = reader.TryWriteInt32(address, 320);
            check(!write.Succeeded && write.Win32Error != 0 && write.BytesTransferred == 0
                && owner.TryReadInt32(address, out observed).Succeeded && observed == int.MinValue,
                "a denied write retains the native error and leaves the owned value unchanged");
            Int32Transfer read = reader.TryReadInt32(0, out _);
            check(!read.Succeeded && read.Win32Error != 0 && read.BytesTransferred == 0,
                "a native read failure retains the error rather than inventing a value");
        }
        finally
        {
            check(owner.Free(address), "own cap I/O test allocation is freed");
        }
    }
}

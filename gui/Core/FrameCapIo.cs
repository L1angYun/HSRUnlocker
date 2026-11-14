namespace StarRailUnlocker.Core
{
    internal readonly record struct Int32Transfer(bool NativeSucceeded, long BytesTransferred, int Win32Error)
    {
        internal bool Succeeded => NativeSucceeded && BytesTransferred == sizeof(int);
        internal bool IsIncompleteCopy => Win32Error == 299
            || (NativeSucceeded && BytesTransferred != sizeof(int));

        internal string DescribeFailure()
        {
            string reason = Win32Error switch
            {
                5 => "Runtime.CapIo.AccessDenied",
                299 => "Runtime.CapIo.Incomplete",
                _ when IsIncompleteCopy => "Runtime.CapIo.Incomplete",
                _ => "Runtime.CapIo.Unknown",
            };
            return L10n.T(Win32Error == 0 ? "Runtime.CapIo.WithoutCode" : "Runtime.CapIo.WithCode",
                L10n.T(reason), BytesTransferred, sizeof(int), Win32Error);
        }
    }

    internal sealed class FrameCapReadFailures
    {
        private int _consecutive;

        internal bool CanRetry(Int32Transfer transfer) => transfer.IsIncompleteCopy && ++_consecutive <= 2;
        internal void Reset() => _consecutive = 0;
    }
}

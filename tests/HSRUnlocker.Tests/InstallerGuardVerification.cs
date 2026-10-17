using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StarRailUnlocker.Core;

internal static class InstallerGuardVerification
{
    public static void Run(Action<bool, string> check)
    {
        string name = @"Global\HSRUnlocker.Tests." + Guid.NewGuid().ToString("N");
        check(!CanOpen(name), "an own random global installer guard is absent before creation");

        using (var first = new InstallerRunningGuard(name))
        {
            check(CanOpen(name), "the installer can detect a live global guard with SYNCHRONIZE access");
            using (var second = new InstallerRunningGuard(name))
            {
                first.Dispose();
                check(CanOpen(name), "a second app handle keeps the global guard alive after the first closes");
            }
            check(!CanOpen(name), "the global guard disappears after its last app handle closes");
            first.Dispose();
            check(!CanOpen(name), "repeated guard disposal does not recreate or retain a named object");
        }

        using (var reopened = new InstallerRunningGuard(name))
        {
            check(CanOpen(name), "the same own fixture name can be created after complete cleanup");
        }
        check(!CanOpen(name), "a recreated guard also releases its native handle");
    }

    private static bool CanOpen(string name)
    {
        using var handle = OpenMutexW(0x00100000, false, name);
        int error = Marshal.GetLastWin32Error();
        if (!handle.IsInvalid) return true;
        if (error == 2) return false;
        throw new Win32Exception(error, "Could not inspect an own random installer guard fixture.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle OpenMutexW(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
}

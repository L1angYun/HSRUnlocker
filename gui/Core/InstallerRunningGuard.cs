using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StarRailUnlocker.Core
{
    internal sealed class InstallerRunningGuard : IDisposable
    {
        private const string MutexName = @"Global\HSRUnlocker.Running";
        private const uint Synchronize = 0x00100000;
        private readonly SafeWaitHandle _handle;

        public InstallerRunningGuard() : this(MutexName) { }

        internal InstallerRunningGuard(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(@"Global\", StringComparison.Ordinal))
                throw new ArgumentException("An installer guard must use the global mutex namespace.", nameof(name));

            // Installers in other sessions need detection access, but never mutex ownership.
            const string descriptor = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100000;;;WD)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor, 1, out var security, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create installer guard security.");
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    Descriptor = security,
                    InheritHandle = false
                };
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var created = CreateMutexW(ref attributes, false, name);
                    int error = Marshal.GetLastWin32Error();
                    if (!created.IsInvalid) { _handle = created; return; }
                    created.Dispose();
                    if (error != 5)
                        throw new Win32Exception(error, "Could not create the global installer running guard.");

                    // CreateMutex asks for ALL_ACCESS on an existing object. An unelevated
                    // holder can still retain the same object using the detection-only ACL.
                    var existing = OpenMutexW(Synchronize, false, name);
                    error = Marshal.GetLastWin32Error();
                    if (!existing.IsInvalid) { _handle = existing; return; }
                    existing.Dispose();
                    if (error != 2)
                        throw new Win32Exception(error, "Could not open the global installer running guard.");
                }
                throw new InvalidOperationException("The global installer running guard changed repeatedly during startup.");
            }
            finally { LocalFree(security); }
        }

        public void Dispose() => _handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public uint Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string descriptor, uint revision, out IntPtr security, out uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeWaitHandle CreateMutexW(
            ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeWaitHandle OpenMutexW(
            uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}

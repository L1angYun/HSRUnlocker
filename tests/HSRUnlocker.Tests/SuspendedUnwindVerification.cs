using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using StarRailUnlocker.Core;

internal static class SuspendedUnwindVerification
{
    public static void Run(Action<bool, string> check)
    {
        ProcessInformation child = StartOwnSuspendedHelp();
        TargetProcess target = null;
        RemoteFunctionTable registration = null;
        IntPtr worker = IntPtr.Zero;
        ulong state = 0;
        bool cleanExit = false;
        try
        {
            check(child.ProcessId != (uint)Environment.ProcessId &&
                  Native.WaitForSingleObject(child.Process, 0) == 258 &&
                  Native.WaitForSingleObject(child.Thread, 0) == 258,
                "unwind startup fixture owns a fresh suspended same-assembly help child");

            ulong resolved = 0;
            Exception resolveError = null;
            using (var readOnly = new TargetProcess(child.ProcessId, readOnly: true))
            using (var resolver = new RemoteFunctionTable(readOnly, 0x10000))
            {
                MethodInfo method = typeof(RemoteFunctionTable).GetMethod("ResolveAddFunctionTable",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Cannot inspect the production system-export resolver.");
                try { resolved = (ulong)method.Invoke(resolver, null); }
                catch (TargetInvocationException ex) { resolveError = ex.InnerException ?? ex; }
                if (resolveError != null)
                    Console.Error.WriteLine("Suspended child export resolution: " + resolveError.Message);
                check(resolveError == null && resolved != 0,
                    "system unwind export resolves before the owned suspended child's loader initializes");
                VerifyResolvedExport(readOnly, resolved, check);
            }

            target = new TargetProcess(child.ProcessId);
            state = target.Allocate(0x1000);
            ulong code = target.Allocate(0x1000);
            byte[] bootstrap = MobileGameLauncher.BuildBootstrap();
            check(state != 0 && code != 0 && target.WriteData(state, new byte[64]) &&
                  target.WriteData(code, bootstrap) && target.MakeExecutable(code, 0x1000),
                "suspended unwind fixture prepares only owned RW state and RX production bootstrap pages");

            registration = new RemoteFunctionTable(target, code, bootstrap, MobileGameLauncher.BuildBootstrapUnwindInfo());
            registration.Register();
            check(registration.IsRegistered && registration.MustRetainCode,
                "production unwind registration succeeds while the own child's primary thread remains suspended");

            worker = Native.CreateRemoteThread(target.Handle, IntPtr.Zero, UIntPtr.Zero,
                (IntPtr)(long)code, (IntPtr)(long)state, 0, out _);
            if (worker == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the own cancelled bootstrap worker.");
            var wait = Stopwatch.StartNew();
            while (target.ReadInt32(state + 4) != 1 && wait.ElapsedMilliseconds < 5000 &&
                   target.IsAlive && Native.WaitForSingleObject(worker, 0) == 258)
                Thread.Sleep(1);
            check(target.ReadInt32(state + 4) == 1 &&
                  Native.WaitForSingleObject(worker, 0) == 258 &&
                  target.ReadUInt64(state + 24) == 0 && target.ReadInt32(state + 32) == 0,
                "registered production bootstrap reaches its ready gate without calling an application entry");
            check(target.WriteInt32(state, 2) && Native.WaitForSingleObject(worker, 5000) == 0 &&
                  target.ReadInt32(state + 4) == 1 && target.ReadInt32(state + 32) == 0,
                "cancelling the own bootstrap gate settles the worker without running an application entry");
            byte[] unchanged = target.Read(code, bootstrap.Length);
            check(unchanged != null && unchanged.AsSpan().SequenceEqual(bootstrap),
                "suspended registration and cancellation leave the production bootstrap bytes unchanged");
        }
        finally
        {
            try
            {
                // Cancel before resuming the exact owned primary, including partial-start failure paths.
                if (target != null && state != 0 && target.IsAlive && !target.WriteInt32(state, 2))
                    throw new InvalidOperationException("Cannot cancel the own bootstrap during fixture cleanup.");
            }
            finally
            {
                try
                {
                    if (Native.WaitForSingleObject(child.Process, 0) == 258 &&
                        ResumeThread(child.Thread) != 1)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the exact own help-child primary.");
                    if (Native.WaitForSingleObject(child.Process, 15000) != 0)
                        throw new TimeoutException("Own help child did not exit after its primary was resumed; no force-kill was attempted.");
                    cleanExit = Native.GetExitCodeProcess(child.Process, out uint exitCode) != 0 && exitCode == 0;
                    if (!cleanExit)
                        throw new InvalidOperationException("Own help child exited unexpectedly instead of returning success.");
                    if (worker != IntPtr.Zero && Native.WaitForSingleObject(worker, 0) != 0)
                        throw new InvalidOperationException("Own bootstrap worker is not settled after child exit.");
                    Console.WriteLine("CLEANUP: own suspended help child exited normally; no force-kill was attempted.");
                }
                finally
                {
                    // Registration storage and bootstrap pages remain valid through the child's exit.
                    registration?.Dispose();
                    target?.Dispose();
                    if (worker != IntPtr.Zero) Native.CloseHandle(worker);
                    Native.CloseHandle(child.Thread);
                    Native.CloseHandle(child.Process);
                }
            }
        }
        check(cleanExit, "own suspended unwind child resumes into help and exits zero with its workers settled");
    }

    private static void VerifyResolvedExport(TargetProcess target, ulong resolved, Action<bool, string> check)
    {
        IntPtr localBase = GetModuleHandleW("ntdll.dll");
        IntPtr localEntry = GetProcAddress(localBase, "RtlAddFunctionTable");
        if (localBase == IntPtr.Zero || localEntry == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the own ntdll export.");
        ulong rva = checked((ulong)localEntry.ToInt64() - (ulong)localBase.ToInt64());
        UIntPtr queried = VirtualQueryEx(target.Handle, (IntPtr)(long)resolved,
            out var information, (UIntPtr)Marshal.SizeOf<MemoryInformation>());
        string localName = MappedFilename(GetCurrentProcess(), localBase);
        string targetName = MappedFilename(target.Handle, information.AllocationBase);
        byte[] expected = new byte[16];
        Marshal.Copy(localEntry, expected, 0, expected.Length);
        byte[] actual = target.Read(resolved, 16);
        check(queried != UIntPtr.Zero && information.Type == 0x1000000 &&
              information.State == Native.MEM_COMMIT &&
              resolved == checked((ulong)information.AllocationBase.ToInt64() + rva) &&
              localName.Length != 0 && string.Equals(localName, targetName, StringComparison.OrdinalIgnoreCase) &&
              actual != null && actual.AsSpan().SequenceEqual(expected),
            "pre-loader export points into the matching ntdll image at its named RVA with unchanged entry bytes");
    }

    private static string MappedFilename(IntPtr process, IntPtr address)
    {
        var name = new StringBuilder(32768);
        uint length = K32GetMappedFileNameW(process, address, name, name.Capacity);
        return length > 0 && length < name.Capacity ? name.ToString() : "";
    }

    private static ProcessInformation StartOwnSuspendedHelp()
    {
        string assembly = typeof(SuspendedUnwindVerification).Assembly.Location;
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the current test host.");
        if (string.IsNullOrEmpty(assembly) || Assembly.GetEntryAssembly()?.Location != assembly ||
            assembly.Contains('"') || host.Contains('"'))
            throw new InvalidOperationException("Suspended fixture can only relaunch its exact current test entry assembly.");
        bool dotnet = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (!dotnet && !string.Equals(Path.GetFullPath(host), Path.ChangeExtension(Path.GetFullPath(assembly), ".exe"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Suspended fixture host is not the current assembly's own apphost.");
        var command = new StringBuilder("\"" + host + "\" " + (dotnet ? "\"" + assembly + "\" " : "") + "--help");
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 4 | 0x08000000,
                IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the own suspended help child.");
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal IntPtr Reserved2, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        internal IntPtr BaseAddress, AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal UIntPtr RegionSize;
        internal uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryInformation information, UIntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder name, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}

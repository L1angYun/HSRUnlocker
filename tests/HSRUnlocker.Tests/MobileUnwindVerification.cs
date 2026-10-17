using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class MobileUnwindVerification
{
#if MOBILE_UNWIND_STANDALONE
    private static int Main()
    {
        try
        {
            Run((passed, description) =>
            {
                if (!passed) throw new InvalidOperationException("FAIL: " + description);
                Console.WriteLine("PASS: " + description);
            });
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
#endif

    [DllImport("ntdll.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    [DllImport("ntdll.dll")]
    private static extern IntPtr RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc,
        IntPtr functionEntry, IntPtr context, out IntPtr handlerData, out ulong establisherFrame, IntPtr contextPointers);

    [DllImport("ntdll.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool RtlDeleteFunctionTable(IntPtr functionTable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInformation information, UIntPtr size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    public static void Run(Action<bool, string> check)
    {
        using var process = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong code = process.Allocate(0x1000);
        if (code == 0) throw new InvalidOperationException("Cannot allocate own mobile unwind fixture.");
        IntPtr contextStorage = Marshal.AllocHGlobal(1232 + 15);
        IntPtr context = (IntPtr)((contextStorage.ToInt64() + 15) & ~15L);
        IntPtr stack = Marshal.AllocHGlobal(0x400);
        IntPtr registeredTable = IntPtr.Zero;
        ulong tableAllocation = 0;
        RemoteFunctionTable registration = null;
        try
        {
            byte[] bootstrap = MobileGameLauncher.BuildBootstrap();
            if (!process.WriteData(code, bootstrap) || !process.MakeExecutable(code, 0x1000))
                throw new InvalidOperationException("Cannot prepare own RX mobile bootstrap.");
            check(RtlLookupFunctionEntry(code + 62, out _, IntPtr.Zero) == IntPtr.Zero,
                "unregistered mobile bootstrap exposes the missing caller unwind entry at UnityMain return PC");

            byte[] wrongCode = (byte[])bootstrap.Clone();
            wrongCode[^1] ^= 1;
            using (var rejected = new RemoteFunctionTable(process, code, wrongCode, MobileGameLauncher.BuildBootstrapUnwindInfo()))
            {
                bool refused = false;
                try { rejected.Register(); }
                catch (InvalidOperationException) { refused = true; }
                check(refused && !rejected.IsRegistered && !rejected.MustRetainCode
                    && RtlLookupFunctionEntry(code + 62, out _, IntPtr.Zero) == IntPtr.Zero,
                    "a mismatch in the final bootstrap byte refuses registration before publishing any unwind references");
            }
            registration = new RemoteFunctionTable(process, code, bootstrap, MobileGameLauncher.BuildBootstrapUnwindInfo());
            registration.Register();
            registeredTable = RtlLookupFunctionEntry(code + 62, out ulong imageBase, IntPtr.Zero);
            check(registeredTable != IntPtr.Zero && registration.IsRegistered && registration.MustRetainCode,
                "production mobile bootstrap metadata registers the actual UnityMain return PC with the OS");
            if (VirtualQuery(registeredTable, out var information, (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
                throw new InvalidOperationException("Cannot inspect own mobile unwind table.");
            tableAllocation = unchecked((ulong)information.AllocationBase.ToInt64());

            const ulong returnAddress = 0x0000123456789ABC, savedRbx = 0x3131313131313131, currentRbx = 0x7A7A7A7A7A7A7A7A;
            ulong initialRsp = ((unchecked((ulong)stack.ToInt64()) + 0x200) & ~15UL) + 8;
            Marshal.WriteInt64((IntPtr)(long)initialRsp, unchecked((long)returnAddress));
            Marshal.WriteInt64((IntPtr)(long)(initialRsp - 8), unchecked((long)savedRbx));
            // Literal offsets/depths follow PUSH RBX; SUB RSP,20h and the actual CALL at +60.
            foreach (var point in new (int Offset, int Depth, ulong ExpectedRbx)[]
            {
                (0, 0, currentRbx), (1, 8, savedRbx), (5, 40, savedRbx), (8, 40, savedRbx),
                (15, 40, savedRbx), (60, 40, savedRbx), (62, 40, savedRbx), (86, 40, savedRbx)
            })
            {
                Marshal.Copy(new byte[1232], 0, context, 1232);
                Marshal.WriteInt32(context, 48, 0x00100003); // AMD64 CONTROL | INTEGER.
                Marshal.WriteInt64(context, 144, unchecked((long)currentRbx));
                Marshal.WriteInt64(context, 152, unchecked((long)(initialRsp - (ulong)point.Depth)));
                Marshal.WriteInt64(context, 248, unchecked((long)(code + (ulong)point.Offset)));
                IntPtr function = RtlLookupFunctionEntry(code + (ulong)point.Offset, out ulong foundBase, IntPtr.Zero);
                check(function == registeredTable && foundBase == imageBase,
                    $"mobile bootstrap+{point.Offset} resolves to the complete registered bootstrap range");
                RtlVirtualUnwind(0, imageBase, code + (ulong)point.Offset, function, context, out _, out _, IntPtr.Zero);
                check(unchecked((ulong)Marshal.ReadInt64(context, 248)) == returnAddress
                    && unchecked((ulong)Marshal.ReadInt64(context, 152)) == initialRsp + 8
                    && unchecked((ulong)Marshal.ReadInt64(context, 144)) == point.ExpectedRbx,
                    $"mobile bootstrap+{point.Offset} unwinds caller RIP/RSP and the saved RBX across prologue, body or epilogue");
            }
            check(RtlLookupFunctionEntry(code + (ulong)bootstrap.Length, out _, IntPtr.Zero) == IntPtr.Zero,
                "mobile bootstrap unwind coverage ends after the complete bootstrap");
            registration.Dispose();
            check(registration.MustRetainCode && RtlLookupFunctionEntry(code + 62, out _, IntPtr.Zero) == registeredTable,
                "mobile registration disposal retains bootstrap references even without a running bootstrap thread");
        }
        finally
        {
            registration?.Dispose();
            bool unregistered = registeredTable != IntPtr.Zero
                ? RtlDeleteFunctionTable(registeredTable)
                : registration?.MustRetainCode != true;
            if (unregistered)
            {
                if (tableAllocation != 0) process.Free(tableAllocation);
                process.Free(code);
            }
            Marshal.FreeHGlobal(stack);
            Marshal.FreeHGlobal(contextStorage);
            if (!unregistered) throw new InvalidOperationException("Own mobile unwind table removal failed; reachable allocations were retained.");
        }
    }
}

using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class GatewayUnwindVerification
{
#if GATEWAY_UNWIND_STANDALONE
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
        ulong region = process.Allocate(0x1000);
        if (region == 0) throw new InvalidOperationException("Cannot allocate own gateway unwind fixture.");
        IntPtr contextStorage = Marshal.AllocHGlobal(1232 + 15);
        IntPtr context = (IntPtr)((contextStorage.ToInt64() + 15) & ~15L);
        IntPtr stack = Marshal.AllocHGlobal(0x400);
        IntPtr registeredTable = IntPtr.Zero;
        ulong tableAllocation = 0;
        RemoteFunctionTable registration = null;
        try
        {
            ulong gateway = region + 0x100, entry = region + 0x200;
            byte[] prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };
            byte[] code = AntiFadePatcher.BuildGateway(gateway, entry, prologue);
            if (!process.WriteData(gateway, code) || !process.MakeExecutable(region, 0x1000))
                throw new InvalidOperationException("Cannot prepare own RX gateway unwind fixture.");
            check(RtlLookupFunctionEntry(gateway, out _, IntPtr.Zero) == IntPtr.Zero,
                "own private gateway starts without a function entry, exposing the displaced-prologue unwind regression");

            registration = new RemoteFunctionTable(process, gateway);
            registration.Register();
            registeredTable = RtlLookupFunctionEntry(gateway, out ulong imageBase, IntPtr.Zero);
            check(registeredTable != IntPtr.Zero && registration.IsRegistered && registration.MustRetainGateway,
                "production gateway registration publishes a real OS function entry before native entry publication");
            if (VirtualQuery(registeredTable, out var information, (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
                throw new InvalidOperationException("Cannot inspect own registered gateway table allocation.");
            tableAllocation = unchecked((ulong)information.AllocationBase.ToInt64());

            const ulong returnAddress = 0x0000123456789ABC;
            int[] contextOffsets = { 232, 168, 176, 160, 144 }; // R14, RSI, RDI, RBP, RBX.
            ulong[] saved = { 0x1414141414141414, 0x6161616161616161, 0x7171717171717171,
                0x5151515151515151, 0x3131313131313131 };
            ulong initialRsp = ((unchecked((ulong)stack.ToInt64()) + 0x200) & ~15UL) + 8;
            Marshal.WriteInt64((IntPtr)(long)initialRsp, unchecked((long)returnAddress));
            for (int i = 0; i < saved.Length; i++)
                Marshal.WriteInt64((IntPtr)(long)(initialRsp - (ulong)(i + 1) * 8), unchecked((long)saved[i]));

            // These stack depths follow the authored instructions, independently of the unwind encoder.
            foreach (var point in new (int Offset, int Pushes, int Depth)[] { (0, 0, 0), (2, 1, 8), (3, 2, 16),
                (4, 3, 24), (5, 4, 32), (6, 5, 40), (10, 5, 120), (13, 5, 120) })
            {
                Marshal.Copy(new byte[1232], 0, context, 1232);
                Marshal.WriteInt32(context, 48, 0x0010000B); // AMD64 CONTROL | INTEGER | FLOATING_POINT.
                Marshal.WriteInt64(context, 152, unchecked((long)(initialRsp - (ulong)point.Depth)));
                Marshal.WriteInt64(context, 248, unchecked((long)(gateway + (ulong)point.Offset)));
                for (int i = 0; i < saved.Length; i++)
                    Marshal.WriteInt64(context, contextOffsets[i], unchecked((long)(saved[i] ^ 0xFF)));

                IntPtr function = RtlLookupFunctionEntry(gateway + (ulong)point.Offset, out ulong foundBase, IntPtr.Zero);
                check(function == registeredTable && foundBase == imageBase,
                    $"gateway+{point.Offset} resolves to its own registered function entry");
                RtlVirtualUnwind(0, imageBase, gateway + (ulong)point.Offset, function, context, out _, out _, IntPtr.Zero);
                bool restored = unchecked((ulong)Marshal.ReadInt64(context, 248)) == returnAddress
                    && unchecked((ulong)Marshal.ReadInt64(context, 152)) == initialRsp + 8;
                for (int i = 0; i < saved.Length; i++)
                    restored &= unchecked((ulong)Marshal.ReadInt64(context, contextOffsets[i]))
                        == (i < point.Pushes ? saved[i] : saved[i] ^ 0xFF);
                check(restored, $"gateway+{point.Offset} unwinds the exact caller RIP/RSP and only the nonvolatile registers already saved");
            }
            check(RtlLookupFunctionEntry(gateway + 19, out _, IntPtr.Zero) == IntPtr.Zero,
                "gateway unwind range stops immediately after the complete conditional relative jump");
            registration.Register();
            check(RtlLookupFunctionEntry(gateway, out _, IntPtr.Zero) == registeredTable,
                "repeated gateway registration reuses the registered table without publishing a duplicate");
            registration.Dispose();
            check(registration.MustRetainGateway && RtlLookupFunctionEntry(gateway, out _, IntPtr.Zero) == registeredTable,
                "disposing a registered gateway owner retains all table and gateway references until target exit");
        }
        finally
        {
            registration?.Dispose();
            bool unregistered = registeredTable == IntPtr.Zero
                ? registration?.MustRetainGateway != true : RtlDeleteFunctionTable(registeredTable);
            if (unregistered)
            {
                if (tableAllocation != 0) process.Free(tableAllocation);
                process.Free(region);
            }
            Marshal.FreeHGlobal(stack);
            Marshal.FreeHGlobal(contextStorage);
            if (!unregistered) throw new InvalidOperationException("Own gateway table could not be removed; reachable allocations were retained.");
        }
    }
}

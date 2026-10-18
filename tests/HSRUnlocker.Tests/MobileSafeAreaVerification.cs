using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class MobileSafeAreaVerification
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Getter(IntPtr result);

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

    private sealed class RegisteredCode
    {
        public RemoteFunctionTable Owner;
        public IntPtr Table;
        public ulong Storage;
    }

    public static void Run(Action<bool, string> check)
    {
        bool zeroRefused = false;
        try { MobileSafeArea.BuildGetter(0, 5); }
        catch (ArgumentException) { zeroRefused = true; }
        check(zeroRefused, "safe-area builder rejects a null original getter");

        using var process = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong region = process.Allocate(0x5000);
        if (region == 0) throw new InvalidOperationException("Cannot allocate own safe-area fixture.");
        var registrations = new List<RegisteredCode>();
        IntPtr contextStorage = IntPtr.Zero, stack = IntPtr.Zero;
        try
        {
            ulong original = region + 0x1000, caller = original + 0x200;
            ulong custom = region + 0x2000, zero = region + 0x3000, maximum = region + 0x4000;
            byte[] originalCode = BuildOriginal(region);
            byte[] callerCode = BuildCaller(region);
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 15.0001 })
            {
                bool refused = false;
                try { MobileSafeArea.BuildGetter(original, invalid); }
                catch (ArgumentException) { refused = true; }
                check(refused, $"safe-area builder rejects horizontal percentage {invalid} before creating native code");
            }
            byte[] customCode = MobileSafeArea.BuildGetter(original, 5),
                zeroCode = MobileSafeArea.BuildGetter(original, 0), maximumCode = MobileSafeArea.BuildGetter(original, 15);
            byte[] unwindInfo = MobileSafeArea.BuildUnwindInfo();
            bool bounded = customCode is { Length: > 0 and <= 4096 }
                && zeroCode is { Length: > 0 and <= 4096 } && maximumCode is { Length: > 0 and <= 4096 };
            check(bounded, "safe-area production getters fit their bounded independent executable pages");
            if (!bounded) return;
            if (!process.WriteData(original, originalCode) || !process.WriteData(caller, callerCode)
                || !process.WriteData(custom, customCode) || !process.WriteData(zero, zeroCode)
                || !process.WriteData(maximum, maximumCode) || !process.MakeExecutable(original, 0x4000))
                throw new InvalidOperationException("Cannot initialize own safe-area RX fixture.");
            check(Protection(region) == Native.PAGE_READWRITE
                && Protection(original) == Native.PAGE_EXECUTE_READ
                && Protection(custom) == Native.PAGE_EXECUTE_READ
                && Protection(zero) == Native.PAGE_EXECUTE_READ
                && Protection(maximum) == Native.PAGE_EXECUTE_READ,
                "safe-area fixture keeps mutable Rect state RW and all native code RX without writable executable pages");

            Register(process, caller, callerCode,
                new byte[] { 0x01, 0x05, 0x02, 0x00, 0x05, 0x32, 0x01, 0x30 }, registrations);
            Register(process, custom, customCode, unwindInfo, registrations);
            Register(process, zero, zeroCode, unwindInfo, registrations);
            Register(process, maximum, maximumCode, unwindInfo, registrations);
            Getter invoke = Marshal.GetDelegateForFunctionPointer<Getter>((IntPtr)(long)caller);
            ulong output = region + 0x304;
            check((output & 15) == 4 && (output & 3) == 0,
                "safe-area output fixture is float-aligned but deliberately not SIMD-aligned");

            byte[] Execute(ulong getter, byte[] input, string label)
            {
                var guard = Enumerable.Repeat((byte)0xA5, 80).ToArray();
                int before = process.ReadInt32(region + 16);
                if (!process.WriteData(region, input)
                    || !process.WriteData(region + 40, BitConverter.GetBytes(getter))
                    || !process.WriteData(region + 0x2E4, guard))
                    throw new InvalidOperationException("Cannot reset own safe-area state.");
                invoke((IntPtr)(long)output);
                byte[] after = process.Read(region + 0x2E4, guard.Length);
                check(after != null && after.AsSpan(0, 32).SequenceEqual(guard.AsSpan(0, 32))
                    && after.AsSpan(48, 32).SequenceEqual(guard.AsSpan(48, 32)),
                    $"{label}: production getter writes no bytes outside its 16-byte unaligned Rect");
                check(process.ReadInt32(region + 16) == before + 1,
                    $"{label}: production wrapper calls the original getter exactly once");
                check(process.ReadInt32(region + 20) == 8
                    && process.ReadUInt64(region + 32) == CallerRbx,
                    $"{label}: original getter has Win64 stack alignment and shadow space while caller RBX survives");
                return process.Read(output, 16)
                    ?? throw new InvalidOperationException("Cannot read own safe-area result.");
            }

            void Geometry(ulong getter, byte[] input, float x, float width, string label)
            {
                byte[] actual = Execute(getter, input, label);
                check(Near(BitConverter.ToSingle(actual, 0), x) && Near(BitConverter.ToSingle(actual, 8), width),
                    $"{label}: executed production code returns expected symmetric horizontal geometry");
                check(actual.AsSpan(4, 4).SequenceEqual(input.AsSpan(4, 4))
                    && actual.AsSpan(12, 4).SequenceEqual(input.AsSpan(12, 4)),
                    $"{label}: y and height remain bit-for-bit unchanged");
            }

            Geometry(custom, Rect(0, 0, 1920, 1080), 96, 1728, "1920x1080 at five percent");
            ulong callReturn = process.ReadUInt64(region + 24);
            Geometry(custom, Rect(0, 0, 3440, 1440), 172, 3096, "same wrapper after ultrawide resize");
            Geometry(custom, Rect(40, 20, 1800, 1000), 130, 1620, "existing nonzero safe-area insets");
            Geometry(custom, Rect(40, BitConverter.UInt32BitsToSingle(0x80000000), 1800,
                BitConverter.UInt32BitsToSingle(0x447A0001)), 130, 1620, "signed-zero y and nonround height");
            byte[] zeroInput = Rect(40, 20, 1800, 1000);
            check(Execute(zero, zeroInput, "zero percent boundary").AsSpan().SequenceEqual(zeroInput),
                "zero percent preserves the original Rect exactly");
            ulong zeroCallReturn = process.ReadUInt64(region + 24);
            Geometry(maximum, Rect(0, 0, 1920, 1080), 288, 1344, "fifteen percent boundary");
            ulong maximumCallReturn = process.ReadUInt64(region + 24);

            foreach (var invalid in new (string Label, byte[] Rect)[]
            {
                ("NaN x payload", RectBits(0x7FC12345, 0, 0x44F00000, 0x44870000)),
                ("NaN y payload", RectBits(0, 0x7FC54321, 0x44F00000, 0x44870000)),
                ("NaN width payload", RectBits(0, 0, 0x7FC12345, 0x44870000)),
                ("NaN height payload", RectBits(0, 0, 0x44F00000, 0x7FC54321)),
                ("infinite x", Rect(float.PositiveInfinity, 0, 1920, 1080)),
                ("infinite y", Rect(0, float.NegativeInfinity, 1920, 1080)),
                ("infinite width", Rect(0, 0, float.PositiveInfinity, 1080)),
                ("infinite height", Rect(0, 0, 1920, float.PositiveInfinity)),
                ("negative width", Rect(0, 0, -1, 1080)),
                ("negative height", Rect(0, 0, 1920, -1)),
                ("zero width", Rect(0, 0, 0, 1080)),
                ("zero height", Rect(0, 0, 1920, 0)),
                ("overflowing transformed x", Rect(float.MaxValue, 20, float.MaxValue, 1080))
            })
                check(Execute(custom, invalid.Rect, invalid.Label).AsSpan().SequenceEqual(invalid.Rect),
                    $"{invalid.Label}: production wrapper retains all original Rect bits without partial writes");

            contextStorage = Marshal.AllocHGlobal(1232 + 15);
            IntPtr context = (IntPtr)((contextStorage.ToInt64() + 15) & ~15L);
            stack = Marshal.AllocHGlobal(0x400);
            VerifyUnwind(custom, customCode, callReturn, context, stack, check);
            VerifyUnwind(zero, zeroCode, zeroCallReturn, context, stack, check);
            VerifyUnwind(maximum, maximumCode, maximumCallReturn, context, stack, check);
        }
        finally
        {
            bool removable = true;
            foreach (RegisteredCode registration in registrations)
            {
                registration.Owner.Dispose();
                bool removed = registration.Table != IntPtr.Zero
                    ? RtlDeleteFunctionTable(registration.Table) : !registration.Owner.MustRetainCode;
                removable &= removed;
                if (removed && registration.Storage != 0) process.Free(registration.Storage);
            }
            if (contextStorage != IntPtr.Zero) Marshal.FreeHGlobal(contextStorage);
            if (stack != IntPtr.Zero) Marshal.FreeHGlobal(stack);
            if (removable) check(process.Free(region), "safe-area fixture releases native storage only after removing every OS unwind reference");
            else throw new InvalidOperationException("Own safe-area unwind removal was unconfirmed; reachable native storage retained.");
        }
    }

    private const ulong CallerRbx = 0x3141592653589793;

    private static byte[] BuildOriginal(ulong state)
    {
        var code = new List<byte>();
        void Emit(params byte[] bytes) => code.AddRange(bytes);
        // This authored boundary copies dynamic input, uses all shadow slots, then destroys volatile state.
        Emit(0x49, 0xBA); code.AddRange(BitConverter.GetBytes(state));
        Emit(0x41, 0xFF, 0x42, 0x10, 0x48, 0x89, 0xE0, 0x83, 0xE0, 0x0F,
            0x41, 0x89, 0x42, 0x14, 0x48, 0x8B, 0x04, 0x24, 0x49, 0x89, 0x42, 0x18,
            0x41, 0x0F, 0x10, 0x02, 0x0F, 0x11, 0x01);
        Emit(0x48, 0xB8); code.AddRange(BitConverter.GetBytes(0xA55AA55AA55AA55AUL));
        foreach (byte offset in new byte[] { 8, 16, 24, 32 }) Emit(0x48, 0x89, 0x44, 0x24, offset);
        foreach (byte register in new byte[] { 0xC0, 0xC9, 0xD2, 0xDB, 0xE4, 0xED })
            Emit(0x66, 0x0F, 0x76, register);
        foreach (byte register in new byte[] { 0xB8, 0xB9, 0xBA })
        {
            Emit(0x48, register); code.AddRange(BitConverter.GetBytes(0x1111222233334444UL));
        }
        foreach (byte register in new byte[] { 0xB8, 0xB9, 0xBA, 0xBB })
        {
            Emit(0x49, register); code.AddRange(BitConverter.GetBytes(0x5555666677778888UL));
        }
        Emit(0xC3);
        return code.ToArray();
    }

    private static byte[] BuildCaller(ulong state)
    {
        var code = new List<byte> { 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0xBB };
        code.AddRange(BitConverter.GetBytes(CallerRbx));
        code.AddRange(new byte[] { 0x48, 0xB8 }); code.AddRange(BitConverter.GetBytes(state));
        code.AddRange(new byte[] { 0x48, 0x8B, 0x40, 0x28, 0xFF, 0xD0, 0x49, 0xBA });
        code.AddRange(BitConverter.GetBytes(state));
        code.AddRange(new byte[] { 0x49, 0x89, 0x5A, 0x20, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 });
        return code.ToArray();
    }

    private static void Register(TargetProcess process, ulong address, byte[] code, byte[] unwind,
        List<RegisteredCode> registrations)
    {
        var entry = new RegisteredCode { Owner = new RemoteFunctionTable(process, address, code, unwind) };
        registrations.Add(entry);
        entry.Owner.Register();
        entry.Table = RtlLookupFunctionEntry(address, out _, IntPtr.Zero);
        if (entry.Table == IntPtr.Zero || VirtualQuery(entry.Table, out var memory,
            (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
            throw new InvalidOperationException("Cannot locate own registered safe-area unwind storage.");
        entry.Storage = unchecked((ulong)memory.AllocationBase.ToInt64());
    }

    private static void VerifyUnwind(ulong code, byte[] bytes, ulong callReturn,
        IntPtr context, IntPtr stack, Action<bool, string> check)
    {
        int epilogue = bytes.AsSpan().LastIndexOf(new byte[] { 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 });
        bool shape = bytes.AsSpan().StartsWith(new byte[] { 0x53, 0x48, 0x83, 0xEC, 0x20 })
            && epilogue >= 5 && callReturn >= code + 5 && callReturn <= code + (ulong)epilogue;
        check(shape, "safe-area wrapper has a bounded call return and unwindable RBX/shadow-space frame");
        if (!shape) return;
        IntPtr table = RtlLookupFunctionEntry(callReturn, out ulong imageBase, IntPtr.Zero);
        check(table != IntPtr.Zero, "OS unwind lookup covers the actual original-getter return PC recorded at runtime");
        if (table == IntPtr.Zero) return;
        const ulong returnAddress = 0x123456789ABC, insideRbx = 0x7575757575757575;
        ulong initialRsp = ((unchecked((ulong)stack.ToInt64()) + 0x200) & ~15UL) + 8;
        Marshal.WriteInt64((IntPtr)(long)initialRsp, unchecked((long)returnAddress));
        Marshal.WriteInt64((IntPtr)(long)(initialRsp - 8), unchecked((long)CallerRbx));
        foreach (var point in new (ulong Pc, int Depth, ulong Rbx)[]
        {
            (code, 0, CallerRbx), (code + 1, 8, insideRbx), (code + 5, 40, insideRbx),
            (callReturn, 40, insideRbx), (code + (ulong)epilogue, 40, insideRbx),
            (code + (ulong)epilogue + 4, 8, insideRbx), (code + (ulong)epilogue + 5, 0, CallerRbx)
        })
        {
            Marshal.Copy(new byte[1232], 0, context, 1232);
            Marshal.WriteInt32(context, 48, 0x00100003);
            Marshal.WriteInt64(context, 144, unchecked((long)point.Rbx));
            Marshal.WriteInt64(context, 152, unchecked((long)(initialRsp - (ulong)point.Depth)));
            Marshal.WriteInt64(context, 248, unchecked((long)point.Pc));
            IntPtr function = RtlLookupFunctionEntry(point.Pc, out ulong foundBase, IntPtr.Zero);
            check(function == table && foundBase == imageBase,
                $"safe-area+{point.Pc - code} resolves to its independently registered complete function");
            if (function == IntPtr.Zero) continue;
            RtlVirtualUnwind(0, imageBase, point.Pc, function, context, out _, out _, IntPtr.Zero);
            check(unchecked((ulong)Marshal.ReadInt64(context, 248)) == returnAddress
                && unchecked((ulong)Marshal.ReadInt64(context, 152)) == initialRsp + 8
                && unchecked((ulong)Marshal.ReadInt64(context, 144)) == CallerRbx,
                $"safe-area+{point.Pc - code} restores caller RIP/RSP/RBX across body and epilogue");
        }
        check(RtlLookupFunctionEntry(code + (ulong)bytes.Length, out _, IntPtr.Zero) != table,
            "safe-area unwind range excludes the first byte after the complete generated function");
    }

    private static uint Protection(ulong address)
    {
        if (VirtualQuery((IntPtr)(long)address, out var memory,
            (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
            throw new InvalidOperationException("Cannot inspect own safe-area page protection.");
        return memory.Protect;
    }

    private static bool Near(float actual, float expected) =>
        float.IsFinite(actual) && Math.Abs(actual - expected) <= 0.001f;

    private static byte[] Rect(float x, float y, float width, float height) =>
        RectBits(BitConverter.SingleToUInt32Bits(x), BitConverter.SingleToUInt32Bits(y),
            BitConverter.SingleToUInt32Bits(width), BitConverter.SingleToUInt32Bits(height));

    private static byte[] RectBits(uint x, uint y, uint width, uint height)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(x).CopyTo(bytes, 0);
        BitConverter.GetBytes(y).CopyTo(bytes, 4);
        BitConverter.GetBytes(width).CopyTo(bytes, 8);
        BitConverter.GetBytes(height).CopyTo(bytes, 12);
        return bytes;
    }
}

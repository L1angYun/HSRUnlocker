using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class NativeWrapperVerification
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float Setter(IntPtr camera, float value, IntPtr methodInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInformation information, UIntPtr size);

    private static uint Protection(ulong address)
    {
        if (VirtualQuery((IntPtr)(long)address, out var information, (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
            throw new InvalidOperationException("Cannot inspect own allocation protection: " + Marshal.GetLastWin32Error());
        return information.Protect;
    }

    public static void Run(Action<bool, string> check)
    {
        check(Marshal.SizeOf<Native.PROCESSENTRY32W>() == 568 && Marshal.SizeOf<Native.MODULEENTRY32W>() == 1080,
            "x64 Unicode process/module enumeration structures retain their native layouts");
        using var process = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong region = process.Allocate(0x4000);
        if (region == 0) throw new InvalidOperationException("Cannot allocate own native fixture");
        ulong wrapperAddress = region + 0x1000, original = region + 0x2000, dispatch = original + 0x100;
        ulong pointerSlot = region + 0x80, camera = region + 0x3000, methodInfo = camera + 0x80;
        bool slotInitialized = false;
        try
        {
            byte[] wrapper = FovPatcher.BuildWrapper(wrapperAddress, region);
            // Authored leaf fixture: records R8 and returns the float received in XMM1 via XMM0.
            var setter = new List<byte> { 0x4C, 0x89, 0x05 };
            setter.AddRange(BitConverter.GetBytes(checked((int)((long)(region + 0x38) - (long)(original + 7)))));
            setter.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0xC1, 0xC3 });
            var jump = new List<byte> { 0xFF, 0x25 };
            jump.AddRange(BitConverter.GetBytes(checked((int)((long)pointerSlot - (long)(dispatch + 6)))));
            bool initialized = process.WriteData(region + FovPatcher.OriginalOffset, BitConverter.GetBytes(original))
                && process.WriteData(region + FovPatcher.EnabledOffset, new byte[] { 1 })
                && process.WriteData(region + FovPatcher.DeltaOffset, BitConverter.GetBytes(20f))
                && process.WriteData(wrapperAddress, wrapper) && process.WriteData(original, setter.ToArray())
                && process.WriteData(dispatch, jump.ToArray()) && process.WriteData(pointerSlot, BitConverter.GetBytes(original));
            slotInitialized = initialized;
            check(initialized && process.MakeExecutable(wrapperAddress, 0x2000), "prepare native wrapper and authored setter in own allocated pages");
            check(Protection(region) == Native.PAGE_READWRITE && Protection(camera) == Native.PAGE_READWRITE
                && Protection(wrapperAddress) == Native.PAGE_EXECUTE_READ && Protection(original) == Native.PAGE_EXECUTE_READ,
                "native fixture separates writable state/objects from execute-read wrapper/setter pages");
            var call = Marshal.GetDelegateForFunctionPointer<Setter>((IntPtr)(long)wrapperAddress);
            check(call((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) == 80f,
                "Win64 XMM1 input 60 plus delta 20 reaches the original setter as 80");
            check(process.ReadUInt64(region + FovPatcher.CameraOffset) == camera && process.ReadUInt64(region + 0x38) == methodInfo,
                "dynamically allocated camera RCX and method-info R8 survive the wrapper");
            check(process.ReadUInt64(region + FovPatcher.CounterOffset) == 1, "executed wrapper call increments its diagnostic counter");
            check(process.WriteData(region + FovPatcher.DeltaOffset, BitConverter.GetBytes(30f))
                && call((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) == 90f,
                "writable delta update changes the forwarded value to 90 without modifying code pages");
            check(process.WriteData(region + FovPatcher.EnabledOffset, new byte[] { 0 }), "disable the own-process wrapper before pass-through checks");
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            float payloadNaN = BitConverter.Int32BitsToSingle(0x7FC01234);
            ulong disabledCount = process.ReadUInt64(region + FovPatcher.CounterOffset);
            check(BitConverter.SingleToInt32Bits(call((IntPtr)(long)camera, negativeZero, (IntPtr)(long)methodInfo)) == unchecked((int)0x80000000)
                && BitConverter.SingleToInt32Bits(call((IntPtr)(long)camera, payloadNaN, (IntPtr)(long)methodInfo)) == 0x7FC01234
                && process.ReadUInt64(region + FovPatcher.CounterOffset) == disabledCount,
                "disabled wrapper preserves negative zero/NaN payload and does not count bypassed calls");
            check(process.WriteData(region + FovPatcher.EnabledOffset, new byte[] { 1 })
                && process.WriteData(region + FovPatcher.DeltaOffset, BitConverter.GetBytes(20f)), "restore enabled delta 20 in writable fixture state");
            bool unalignedRefused = false;
            try { process.CompareExchangePointer(pointerSlot + 1, original, wrapperAddress, out _); }
            catch (ArgumentException) { unalignedRefused = true; }
            check(unalignedRefused && process.ReadUInt64(pointerSlot) == original, "unaligned pointer exchange is refused before changing the own slot");
            check(process.CompareExchangePointer(pointerSlot, original, wrapperAddress, out var before) && before == original
                && process.ReadUInt64(pointerSlot) == wrapperAddress, "aligned target-side CAS installs the wrapper in the own pointer slot");
            check(!process.CompareExchangePointer(pointerSlot, original, dispatch, out var conflict) && conflict == wrapperAddress
                && process.ReadUInt64(pointerSlot) == wrapperAddress, "CAS conflict returns the observed pointer and preserves current ownership");
            var throughSlot = Marshal.GetDelegateForFunctionPointer<Setter>((IntPtr)(long)dispatch);
            check(throughSlot((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) == 80f, "indirect pointer-slot dispatch invokes the wrapper");
            ulong startCount = process.ReadUInt64(region + FovPatcher.CounterOffset);
            Parallel.For(0, 10000, _ =>
            {
                if (throughSlot((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) != 80f)
                    throw new InvalidOperationException("Incorrect concurrent native result");
            });
            check(process.ReadUInt64(region + FovPatcher.CounterOffset) == startCount + 10000,
                "10000 concurrent own-process native calls preserve arithmetic and the exact atomic count");
            check(process.WriteData(region + FovPatcher.EnabledOffset, new byte[] { 0 })
                && process.CompareExchangePointer(pointerSlot, wrapperAddress, original, out _)
                && process.ReadUInt64(pointerSlot) == original
                && throughSlot((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) == 60f,
                "disable and restore the original pointer; subsequent indirect calls pass through");
            check(call((IntPtr)(long)camera, 60f, (IntPtr)(long)methodInfo) == 60f,
                "previously loaded wrapper pointer remains a valid pass-through after restoration");
            check(!process.HasPendingPointerExchange, "all own-process pointer-exchange helper threads have settled");
        }
        finally
        {
            process.WriteData(region + FovPatcher.EnabledOffset, new byte[] { 0 });
            if (process.HasPendingPointerExchange) process.ReconcilePointerExchange();
            if (!process.HasPendingPointerExchange && slotInitialized && process.ReadUInt64(pointerSlot) == wrapperAddress)
                process.CompareExchangePointer(pointerSlot, wrapperAddress, original, out _);
            // A pending helper may still hold an address in this allocation: retain it until this test process exits.
            if (!process.HasPendingPointerExchange)
                check(process.Free(region), "release only the own native fixture after synchronous calls and helper threads finish");
        }
    }
}

using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class AntiFadeWrapperVerification
{
    private static readonly byte[] Prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float FadeCall(IntPtr instance, float alpha, int kind, ulong flags);

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
            throw new InvalidOperationException("Cannot inspect own anti-fade fixture protection: " + Marshal.GetLastWin32Error());
        return information.Protect;
    }

    public static void Run(Action<bool, string> check)
    {
        using var process = new TargetProcess(checked((uint)Environment.ProcessId));
        ulong region = process.Allocate(0x4000);
        if (region == 0) throw new InvalidOperationException("Cannot allocate own anti-fade wrapper fixture.");
        ulong wrapperAddress = region + 0x1000, gatewayAddress = region + 0x2000;
        ulong entryAddress = region + 0x2100, instance = region + 0x3000;
        ulong[] hotfixFlags = Enumerable.Range(0, 8).Select(i => region + 0x100 + (ulong)i).ToArray();
        try
        {
            byte[] wrapper = AntiFadePatcher.BuildWrapper(wrapperAddress, region, hotfixFlags);
            byte[] gateway = AntiFadePatcher.BuildGateway(gatewayAddress, entryAddress, Prologue);
            // Own authored Win64 entry: record XMM1/R8D/R9/RCX, count all forwarded calls, return XMM1.
            // Replay/unwind all five nonvolatile pushes and the complete SUB/ADD RSP,0x50 pair.
            byte[] body = { 0xF3,0x0F,0x11,0x09, 0x44,0x89,0x41,0x04, 0x4C,0x89,0x49,0x08,
                0x48,0x89,0x49,0x10, 0xF0,0x48,0xFF,0x41,0x18, 0xF3,0x0F,0x10,0xC1,
                0x48,0x83,0xC4,0x50, 0x5B,0x5D,0x5F,0x5E,0x41,0x5E,0xC3 };
            byte[] entry = Prologue.Concat(body).ToArray();
            check(gateway.Length == 19 && gateway.AsSpan(0, 10).SequenceEqual(Prologue)
                && gateway.AsSpan(10, 5).SequenceEqual(new byte[] { 0x48, 0x39, 0xE4, 0x0F, 0x84 })
                && checked((long)gatewayAddress + gateway.Length + BitConverter.ToInt32(gateway, 15)) == checked((long)entryAddress + 10),
                "production anti-fade gateway copies the complete ten-byte prologue and conditionally transfers exactly to entry+10 without tail-epilogue scanning");
            if (wrapper.Length == 0 || wrapper.Length > 0x1000)
                throw new InvalidOperationException("Anti-fade fixture wrapper exceeds its own reserved code page.");
            bool initialized = process.WriteData(region + AntiFadePatcher.EnabledOffset, new byte[] { 1 })
                && process.WriteData(region + AntiFadePatcher.OneOffset, BitConverter.GetBytes(1f))
                && process.WriteData(region + AntiFadePatcher.GatewayPointerOffset, BitConverter.GetBytes(gatewayAddress))
                && process.WriteData(wrapperAddress, wrapper) && process.WriteData(gatewayAddress, gateway)
                && process.WriteData(entryAddress, entry) && process.MakeExecutable(wrapperAddress, 0x2000);
            check(initialized && Protection(region) == Native.PAGE_READWRITE && Protection(instance) == Native.PAGE_READWRITE
                && Protection(wrapperAddress) == Native.PAGE_EXECUTE_READ && Protection(gatewayAddress) == Native.PAGE_EXECUTE_READ
                && Protection(entryAddress) == Native.PAGE_EXECUTE_READ,
                "production anti-fade wrapper/gateway and authored entry occupy RX pages separate from own RW state/object/flags");

            var call = Marshal.GetDelegateForFunctionPointer<FadeCall>((IntPtr)(long)wrapperAddress);
            var original = Marshal.GetDelegateForFunctionPointer<FadeCall>((IntPtr)(long)entryAddress);
            ulong forwarded = 0, cameraCalls = 0, otherCalls = 0;
            void Verify(FadeCall target, float alpha, int kind, ulong flags, float expected, bool camera, bool other, string description)
            {
                float result = target((IntPtr)(long)instance, alpha, kind, flags);
                forwarded++;
                if (camera) cameraCalls++;
                if (other) otherCalls++;
                byte[] received = process.Read(instance, 32) ?? throw new InvalidOperationException("Cannot read own authored fade-call record.");
                check(BitConverter.SingleToInt32Bits(result) == BitConverter.SingleToInt32Bits(expected)
                    && BitConverter.ToInt32(received, 0) == BitConverter.SingleToInt32Bits(expected)
                    && BitConverter.ToInt32(received, 4) == kind && BitConverter.ToUInt64(received, 8) == flags
                    && BitConverter.ToUInt64(received, 16) == instance && BitConverter.ToUInt64(received, 24) == forwarded
                    && process.ReadUInt64(region + AntiFadePatcher.CameraCallsOffset) == cameraCalls
                    && process.ReadUInt64(region + AntiFadePatcher.OtherCallsOffset) == otherCalls, description);
            }
            void SetByte(ulong address, byte value)
            {
                if (!process.WriteData(address, new[] { value }) || process.Read(address, 1) is not { } readback || readback[0] != value)
                    throw new InvalidOperationException("Cannot update own anti-fade fixture data byte.");
            }

            Verify(original, 0.25f, 1, 0x1122334455667788UL, 0.25f, false, false,
                "unwrapped authored fade entry receives/returns 0.25 with kind, RCX, full R9 flags and exact forwarding counter intact");
            Verify(call, 0.25f, 1, 0xFEDCBA9876543210UL, 1f, true, false,
                "production enabled anti-fade wrapper forces kind-1 alpha to 1 and preserves RCX/kind/flags through the real gateway");
            check(process.ReadUInt64(region + AntiFadePatcher.ObjectOffset) == instance
                && process.ReadInt32(region + AntiFadePatcher.InputOffset) == BitConverter.SingleToInt32Bits(0.25f)
                && process.ReadInt32(region + AntiFadePatcher.OutputOffset) == BitConverter.SingleToInt32Bits(1f),
                "production camera diagnostics record original RCX, input 0.25 and forced output 1");
            Verify(call, 0.25f, 2, 0x8123456789ABCDEFUL, 0.25f, false, true,
                "production anti-fade wrapper preserves kind-2 alpha 0.25 and increments only the other-kind counter");
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            Verify(call, negativeZero, -1, ulong.MaxValue, negativeZero, false, true,
                "non-camera kind preserves negative-zero bits, signed R8D kind and all R9 flag bits without camera counting");

            SetByte(region + AntiFadePatcher.EnabledOffset, 0);
            Verify(call, 0.25f, 1, 0x0102030405060708UL, 0.25f, false, false,
                "disabled production wrapper passes kind-1 alpha through and leaves both diagnostic counters unchanged");
            float payloadNaN = BitConverter.Int32BitsToSingle(0x7FC01234);
            Verify(call, payloadNaN, 2, 0x8877665544332211UL, payloadNaN, false, false,
                "disabled production wrapper preserves NaN payload/other-kind arguments and does not count bypassed calls");
            SetByte(region + AntiFadePatcher.EnabledOffset, 1);

            for (int i = 0; i < hotfixFlags.Length; i++)
            {
                // Exactly one own flag is nonzero at a time; include the high bit to exercise byte != 0, not == 1.
                SetByte(hotfixFlags[i], i % 2 == 0 ? (byte)1 : (byte)0x80);
                try
                {
                    Verify(call, 0.25f, 1, 0xF000000000000000UL + (ulong)i, 0.25f, false, false,
                        $"live hotfix flag {i + 1}/8 independently forces pass-through with RCX/flags/forward count intact and no diagnostic increments");
                }
                finally { SetByte(hotfixFlags[i], 0); }
                Verify(call, 0.25f, 1, 0xA500000000000000UL + (ulong)i, 1f, true, false,
                    $"clearing hotfix flag {i + 1}/8 immediately restores kind-1 forcing and exact camera counting without code changes");
            }
            check(Protection(region) == Native.PAGE_READWRITE && Protection(instance) == Native.PAGE_READWRITE
                && Protection(wrapperAddress) == Native.PAGE_EXECUTE_READ && Protection(gatewayAddress) == Native.PAGE_EXECUTE_READ
                && Protection(entryAddress) == Native.PAGE_EXECUTE_READ
                && process.Read(wrapperAddress, wrapper.Length) is { } finalWrapper && finalWrapper.AsSpan().SequenceEqual(wrapper)
                && process.Read(gatewayAddress, gateway.Length) is { } finalGateway && finalGateway.AsSpan().SequenceEqual(gateway),
                "all production wrapper toggles/hotfix guards update only own RW data and leave RX code bytes/protections unchanged");
        }
        finally
        {
            process.WriteData(region + AntiFadePatcher.EnabledOffset, new byte[] { 0 });
            // There are no helper threads, installed entries or outstanding asynchronous calls in this fixture.
            check(process.Free(region), "free only the own anti-fade fixture after every synchronous native call has returned");
        }
    }
}

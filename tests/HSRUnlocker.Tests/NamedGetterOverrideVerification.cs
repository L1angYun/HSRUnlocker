using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using StarRailUnlocker.Core;

internal static class NamedGetterOverrideVerification
{
    private const uint ReadOnly = 0x02;
    private const int ImageSize = 0x8000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int OwnGetter();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInformation information, UIntPtr size);

    [DllImport("ntdll.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    [DllImport("ntdll.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool RtlDeleteFunctionTable(IntPtr functionTable);

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
        var installer = typeof(MobileGameLauncher).GetMethod("InstallGetterOverride",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(TargetProcess), typeof(ulong), typeof(PeImage), typeof(GameBuildResolver.NamedRegistration),
                typeof(Func<ulong, byte[]>), typeof(byte[]), typeof(string) }, null);
        check(installer != null && installer.ReturnType == typeof(RemoteFunctionTable),
            "shared named getter installer is present before any native fixture runs");
        if (installer == null) return;

        VerifyPublication(installer, check, withUnwind: false);
        VerifyPublication(installer, check, withUnwind: true);
        VerifyConflicts(installer, check);
        VerifyCompetingPublication(installer, check);
    }

    private static RemoteFunctionTable Install(MethodInfo installer, OwnedImage fixture,
        GameBuildResolver.NamedRegistration registration, Func<ulong, byte[]> builder, byte[] unwind)
    {
        try
        {
            var result = (RemoteFunctionTable)installer.Invoke(null,
                new object[] { fixture.Target, fixture.Base, fixture.Image, registration, builder, unwind, "synthetic safeArea" });
            fixture.TrackInstallation(result);
            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static byte[] BuildOwnForwarder(ulong original, bool withUnwind)
    {
        var code = new List<byte>();
        if (withUnwind) code.AddRange(new byte[] { 0x53, 0x48, 0x83, 0xEC, 0x20 });
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(original));
        code.AddRange(withUnwind
            ? new byte[] { 0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 }
            : new byte[] { 0xFF, 0xE0 });
        return code.ToArray();
    }

    private static void VerifyPublication(MethodInfo installer, Action<bool, string> check, bool withUnwind)
    {
        using var fixture = new OwnedImage(reverseOrder: withUnwind);
        uint previous = Query(fixture.Slot).Protect;
        int builds = 0;
        ulong suppliedOriginal = 0;
        byte[] expectedCode = BuildOwnForwarder(fixture.Original, withUnwind);
        // Independently authored unwind: ALLOC_SMALL 0x20 at +5, PUSH RBX at +1.
        byte[] unwind = withUnwind ? new byte[] { 1, 5, 2, 0, 5, 0x32, 1, 0x30 } : null;
        var owner = Install(installer, fixture, fixture.Registration, original =>
        {
            builds++;
            suppliedOriginal = original;
            return BuildOwnForwarder(original, withUnwind);
        }, unwind);

        string kind = withUnwind ? "nonleaf" : "leaf";
        check(builds == 1 && suppliedOriginal == fixture.Original,
            $"{kind} named getter builder receives the relocated original function exactly once");
        check(fixture.PublishedCode != 0 && fixture.PublishedCode != fixture.Original
            && fixture.Target.ReadUInt64(fixture.Slot) == fixture.PublishedCode,
            $"{kind} named getter publishes its own generated function through the resolved slot");
        check(previous == ReadOnly && Query(fixture.Slot).Protect == previous,
            $"{kind} named getter restores the originally read-only registration page");
        check(Query(fixture.PublishedCode).Protect == Native.PAGE_EXECUTE_READ
            && fixture.Target.Read(fixture.PublishedCode, expectedCode.Length).AsSpan().SequenceEqual(expectedCode),
            $"{kind} named getter is byte-exact RX code rather than writable executable storage");

        IntPtr function = RtlLookupFunctionEntry(fixture.PublishedCode, out ulong unwindBase, IntPtr.Zero);
        if (withUnwind)
        {
            check(owner?.IsRegistered == true && owner.MustRetainCode && function != IntPtr.Zero,
                "published nonleaf named getter has a real registered OS unwind entry");
            byte[] entry = fixture.Target.Read(unchecked((ulong)function.ToInt64()), 12);
            check(entry != null && unwindBase + BitConverter.ToUInt32(entry, 0) == fixture.PublishedCode
                && unwindBase + BitConverter.ToUInt32(entry, 4) == fixture.PublishedCode + (ulong)expectedCode.Length
                && fixture.Target.Read(unwindBase + BitConverter.ToUInt32(entry, 8), unwind.Length).AsSpan().SequenceEqual(unwind),
                "named getter unwind entry covers precisely the authored function and its supplied unwind record");
        }
        else
        {
            check(owner == null && function == IntPtr.Zero,
                "leaf named getter needs neither a function-table owner nor an OS unwind registration");
        }

        // Only the authored constant-return function and our exact forwarder execute here.
        var call = Marshal.GetDelegateForFunctionPointer<OwnGetter>((IntPtr)(long)fixture.PublishedCode);
        check(call() == 42, $"{kind} published wrapper forwards to the authored original function");
        GC.KeepAlive(call);
    }

    private static void VerifyConflicts(MethodInfo installer, Action<bool, string> check)
    {
        // Each mutation models an independent stale-proof or unsafe-slot regression.
        foreach (string conflict in new[] { "entry", "loop", "keys", "native byte 32", "slot",
            "unaligned slot", "executable table", "unreadable table", "slot outside sections" })
        {
            uint flags = conflict == "executable table" ? 0x60000040u
                : conflict == "unreadable table" ? 0x00000040u : 0x40000040u;
            using var fixture = new OwnedImage(tableFlags: flags);
            var registration = fixture.Registration;
            switch (conflict)
            {
                case "entry": fixture.Corrupt(registration.EntryRva + (ulong)registration.Entry.Length - 1); break;
                case "loop": fixture.Corrupt(registration.LoopRva + (ulong)registration.Loop.Length - 1); break;
                case "keys": fixture.Corrupt(registration.KeysRva + (ulong)registration.Keys.Length - 1); break;
                case "native byte 32": fixture.Corrupt(registration.NativeTargetRva + 31); break;
                case "slot": fixture.SetPointer(fixture.Slot, fixture.Original + 0x10); break;
                case "unaligned slot":
                    registration = registration with { TableEntryRva = registration.TableEntryRva + 1 };
                    fixture.SetPointer(fixture.Base + registration.TableEntryRva, fixture.Original);
                    break;
                case "slot outside sections":
                    registration = registration with { TableEntryRva = 0x5000 };
                    fixture.SetPointer(fixture.Base + registration.TableEntryRva, fixture.Original);
                    break;
            }
            fixture.AcceptUnpublishedSlot();
            byte[] before = fixture.Target.Read(fixture.Base, ImageSize);
            ulong slot = fixture.Base + registration.TableEntryRva;
            uint protection = Query(slot).Protect;
            bool built = false, refused = false;
            try
            {
                _ = Install(installer, fixture, registration, original =>
                {
                    built = true;
                    return BuildOwnForwarder(original, false);
                }, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
            { refused = true; }
            check(refused && !built, $"named getter refuses {conflict} conflict before building code");
            check(fixture.Target.Read(fixture.Base, ImageSize).AsSpan().SequenceEqual(before)
                && Query(slot).Protect == protection,
                $"named getter leaves all authored image bytes and slot protection unchanged after {conflict} conflict");
        }
    }

    private static void VerifyCompetingPublication(MethodInfo installer, Action<bool, string> check)
    {
        using var fixture = new OwnedImage();
        ulong competing = fixture.Original + 0x10;
        bool built = false, refused = false;
        try
        {
            _ = Install(installer, fixture, fixture.Registration, original =>
            {
                built = true;
                fixture.SetPointer(fixture.Slot, competing);
                fixture.AcceptUnpublishedSlot();
                return BuildOwnForwarder(original, false);
            }, null);
        }
        catch (InvalidOperationException) { refused = true; }
        check(built && refused && fixture.Target.ReadUInt64(fixture.Slot) == competing,
            "named getter CAS refuses a competing publication made after validation without overwriting it");
        check(Query(fixture.Slot).Protect == ReadOnly && !fixture.Target.HasPendingPointerExchange,
            "failed named getter CAS restores read-only protection and settles its own exchange worker");
    }

    private static MemoryInformation Query(ulong address)
    {
        if (VirtualQuery((IntPtr)(long)address, out var information,
            (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
            throw new InvalidOperationException("Cannot inspect an own named-getter fixture allocation.");
        return information;
    }

    private sealed class OwnedImage : IDisposable
    {
        public TargetProcess Target { get; }
        public ulong Base { get; }
        public PeImage Image { get; }
        public GameBuildResolver.NamedRegistration Registration { get; }
        public ulong Slot => Base + Registration.TableEntryRva;
        public ulong Original => Base + Registration.NativeTargetRva;
        public ulong PublishedCode { get; private set; }
        private readonly string _path;
        private RemoteFunctionTable _owner;
        private ulong _unpublishedSlot;

        public OwnedImage(bool reverseOrder = false, uint tableFlags = 0x40000040)
        {
            Target = new TargetProcess(checked((uint)Environment.ProcessId));
            Base = Target.Allocate(ImageSize);
            _path = Path.Combine(Path.GetTempPath(), "HSRUnlocker-own-named-getter-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                if (Base == 0) throw new InvalidOperationException("Cannot allocate an own named-getter fixture.");
                byte[] bytes = SyntheticPeVerification.BuildNamedGetterImage(Base, reverseOrder);
                // The authored PE has three 40-byte section headers after its 0xF0 optional header.
                BitConverter.GetBytes(tableFlags).CopyTo(bytes, 0x80 + 24 + 0xF0 + 40 + 36);
                File.WriteAllBytes(_path, bytes);
                var initial = new PeImage(_path);
                var named = GameBuildResolver.ResolveNamedRegistration(initial, SyntheticPeVerification.SafeAreaName);
                var text = initial.Sections.Single(section => section.Name == ".text");
                int nativeRaw = checked((int)(text.Raw + named.NativeTargetRva - text.Rva));
                Array.Fill(bytes, (byte)0x90, nativeRaw, 32);
                new byte[] { 0xB8, 42, 0, 0, 0, 0xC3 }.CopyTo(bytes, nativeRaw);
                File.WriteAllBytes(_path, bytes);
                Image = new PeImage(_path);
                Registration = GameBuildResolver.ResolveNamedRegistration(Image, SyntheticPeVerification.SafeAreaName);
                if (!Target.WriteData(Base, bytes.AsSpan(0, 0x200).ToArray()))
                    throw new InvalidOperationException("Cannot map own authored PE headers.");
                foreach (var section in Image.Sections)
                    if (!Target.WriteData(Base + section.Rva,
                        bytes.AsSpan(checked((int)section.Raw), checked((int)section.RawSize)).ToArray()))
                        throw new InvalidOperationException("Cannot map an own authored PE section.");
                Protect(Base, 0x1000, ReadOnly);
                if (!Target.MakeExecutable(Base + 0x1000, 0x1000))
                    throw new InvalidOperationException("Cannot protect own authored native function.");
                Protect(Base + 0x6000, 0x1000, ReadOnly);
                _unpublishedSlot = Original;
            }
            catch
            {
                if (Base != 0) Target.Free(Base);
                Target.Dispose();
                File.Delete(_path);
                throw;
            }
        }

        public void Corrupt(ulong rva)
        {
            byte[] value = Target.Read(Base + rva, 1);
            value[0] ^= 1;
            if (!Target.WriteBytes(Base + rva, value))
                throw new InvalidOperationException("Cannot author an own fixture proof conflict.");
        }

        public void SetPointer(ulong address, ulong value)
        {
            if (address < Base || address - Base > ImageSize - 8)
                throw new ArgumentOutOfRangeException(nameof(address));
            uint previous = Protect(address, 8, Native.PAGE_READWRITE);
            try
            {
                if (!Target.WriteData(address, BitConverter.GetBytes(value)))
                    throw new InvalidOperationException("Cannot write an own fixture slot.");
            }
            finally { Protect(address, 8, previous); }
        }

        public void AcceptUnpublishedSlot() => _unpublishedSlot = Target.ReadUInt64(Slot);

        public void TrackInstallation(RemoteFunctionTable owner)
        {
            _owner = owner;
            ulong pointer = Target.ReadUInt64(Slot);
            if (pointer != _unpublishedSlot) PublishedCode = pointer;
        }

        private uint Protect(ulong address, int size, uint protection)
        {
            if (!Native.VirtualProtectEx(Target.Handle, (IntPtr)(long)address, (IntPtr)size, protection, out uint previous))
                throw new InvalidOperationException("Cannot protect an own named-getter fixture page.");
            return previous;
        }

        public void Dispose()
        {
            bool retain = Target.HasPendingPointerExchange;
            try
            {
                _owner?.Dispose();
                if (!retain && PublishedCode != 0)
                {
                    retain = Target.ReadUInt64(Slot) != PublishedCode;
                    if (!retain)
                    {
                        SetPointer(Slot, Original);
                        IntPtr table = RtlLookupFunctionEntry(PublishedCode, out _, IntPtr.Zero);
                        if (table != IntPtr.Zero)
                        {
                            ulong metadata = unchecked((ulong)Query(unchecked((ulong)table.ToInt64())).AllocationBase.ToInt64());
                            retain = !RtlDeleteFunctionTable(table);
                            if (!retain && !Target.Free(metadata))
                                throw new InvalidOperationException("Cannot release own named-getter unwind metadata.");
                        }
                        else retain = _owner?.MustRetainCode == true;
                        if (!retain && !Target.Free(unchecked((ulong)Query(PublishedCode).AllocationBase.ToInt64())))
                            throw new InvalidOperationException("Cannot release own named-getter code.");
                    }
                }
                else if (!retain) retain = Target.ReadUInt64(Slot) != _unpublishedSlot;
                if (!retain && !Target.Free(Base))
                    throw new InvalidOperationException("Cannot release own authored PE mapping.");
                if (retain)
                    throw new InvalidOperationException("Own named-getter references are uncertain; allocations were retained until test-process exit.");
            }
            finally
            {
                Target.Dispose();
                File.Delete(_path);
            }
        }
    }
}

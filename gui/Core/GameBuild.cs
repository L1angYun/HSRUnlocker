using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StarRailUnlocker.Core
{
    public sealed record PeSection(string Name, uint Rva, uint VirtualSize, uint Raw, uint RawSize, uint Flags);
    public sealed record PeCodeChunk(ulong Rva, byte[] Bytes, int ScanLength);

    /// <summary>Reads bounded PE ranges and streams executable sections; never loads a whole DLL.</summary>
    public sealed class PeImage
    {
        public string Path { get; }
        public uint Timestamp { get; }
        public uint SizeOfImage { get; }
        public ulong PreferredImageBase { get; }
        public IReadOnlyList<PeSection> Sections => _sections.AsReadOnly();
        private readonly List<PeSection> _sections = new();
        private readonly uint _exceptionRva, _exceptionSize;
        private Dictionary<uint, RuntimeFunction> _runtimeFunctions;
        private sealed record RuntimeFunction(uint Begin, uint End, uint Unwind);

        public PeImage(string path)
        {
            Path = path;
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                throw new InvalidDataException("Not a PE image: " + path);
            stream.Position = 0x3C;
            int pe = reader.ReadInt32();
            if (pe < 0x40 || pe > stream.Length - 264) throw new InvalidDataException("Invalid PE header");
            stream.Position = pe;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                throw new InvalidDataException("Expected an x64 PE image");
            ushort sectionCount = reader.ReadUInt16();
            Timestamp = reader.ReadUInt32();
            stream.Position = pe + 20;
            ushort optionalSize = reader.ReadUInt16();
            if (optionalSize < 144 || sectionCount == 0 || sectionCount > 96
                || (long)pe + 24 + optionalSize + sectionCount * 40 > stream.Length)
                throw new InvalidDataException("Invalid PE header sizes");
            stream.Position = pe + 24;
            if (reader.ReadUInt16() != 0x20B) throw new InvalidDataException("Expected PE32+");
            stream.Position = pe + 24 + 24;
            PreferredImageBase = reader.ReadUInt64();
            stream.Position = pe + 24 + 56;
            SizeOfImage = reader.ReadUInt32();
            if (SizeOfImage == 0) throw new InvalidDataException("Empty PE image");
            stream.Position = pe + 24 + 108;
            uint directories = reader.ReadUInt32();
            if (directories >= 4)
            {
                stream.Position = pe + 24 + 112 + 3 * 8;
                _exceptionRva = reader.ReadUInt32();
                _exceptionSize = reader.ReadUInt32();
            }
            for (int i = 0; i < sectionCount; i++)
            {
                stream.Position = pe + 24 + optionalSize + i * 40;
                string name = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                uint virtualSize = reader.ReadUInt32(), rva = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32(), raw = reader.ReadUInt32();
                stream.Position += 12;
                uint flags = reader.ReadUInt32();
                if (rva >= SizeOfImage || (ulong)rva + virtualSize > SizeOfImage
                    || (ulong)raw + rawSize > (ulong)stream.Length)
                    throw new InvalidDataException("PE section is outside its image or file");
                _sections.Add(new(name, rva, virtualSize, raw, rawSize, flags));
            }
        }

        private PeSection FindRange(ulong rva, int size, bool fileBacked)
        {
            if (size < 0 || rva >= SizeOfImage || (ulong)size > SizeOfImage - rva) return null;
            foreach (var section in _sections)
            {
                ulong length = fileBacked ? section.RawSize : Math.Max(section.VirtualSize, section.RawSize);
                if (rva >= section.Rva && rva - section.Rva <= length
                    && (ulong)size <= length - (rva - section.Rva)) return section;
            }
            return null;
        }

        public bool IsFileBackedRva(ulong rva, int size) => FindRange(rva, size, true) != null;
        public bool IsExecutableRva(ulong rva, int size = 1) =>
            FindRange(rva, size, true) is { } section && (section.Flags & 0x20000000) != 0;
        public bool IsWritableRva(ulong rva, int size) =>
            FindRange(rva, size, false) is { } section && (section.Flags & 0x80000000) != 0;

        public byte[] ReadRva(ulong rva, int size)
        {
            if (size < 1 || size > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
            var section = FindRange(rva, size, true);
            if (section == null)
                throw new InvalidDataException($"RVA 0x{rva:X} has no file-backed range in {System.IO.Path.GetFileName(Path)}");
            using var stream = File.OpenRead(Path);
            stream.Position = checked((long)section.Raw + (long)(rva - section.Rva));
            var bytes = new byte[size];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public IEnumerable<PeCodeChunk> ReadExecutableChunks(int chunkSize = 1024 * 1024, int overlap = 24)
        {
            if (chunkSize < 4096 || chunkSize > 1024 * 1024 || overlap < 0 || overlap > 256)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            using var stream = File.OpenRead(Path);
            foreach (var section in _sections)
            {
                if ((section.Flags & 0x20000000) == 0 || section.RawSize == 0) continue;
                ulong length = Math.Min((ulong)section.RawSize, SizeOfImage - (ulong)section.Rva);
                for (ulong offset = 0; offset < length;)
                {
                    int scanLength = (int)Math.Min((ulong)chunkSize, length - offset);
                    int readLength = scanLength + (int)Math.Min((ulong)overlap, length - offset - (ulong)scanLength);
                    var bytes = new byte[readLength];
                    stream.Position = checked((long)section.Raw + (long)offset);
                    stream.ReadExactly(bytes);
                    yield return new(section.Rva + offset, bytes, scanLength);
                    offset += (ulong)scanLength;
                }
            }
        }

        /// <summary>Combines nearby chained x64 unwind entries belonging to this function root.</summary>
        public byte[] ReadFunctionBytes(ulong rva, int maximumBytes = 2048)
        {
            if (maximumBytes < 32 || maximumBytes > 4096 || rva > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (_runtimeFunctions == null)
            {
                if (_exceptionSize == 0 || _exceptionSize > 4 * 1024 * 1024 || _exceptionSize % 12 != 0)
                    throw new NotSupportedException("A bounded x64 exception directory is required to verify camera functions");
                var bytes = ReadRva(_exceptionRva, checked((int)_exceptionSize));
                var functions = new Dictionary<uint, RuntimeFunction>();
                for (int i = 0; i < bytes.Length; i += 12)
                {
                    uint begin = BitConverter.ToUInt32(bytes, i), end = BitConverter.ToUInt32(bytes, i + 4);
                    uint unwind = BitConverter.ToUInt32(bytes, i + 8);
                    if (begin == 0 && end == 0 && unwind == 0) continue;
                    if (end <= begin || !IsExecutableRva(begin, checked((int)(end - begin)))
                        || !IsFileBackedRva(unwind, 4) || !functions.TryAdd(begin, new(begin, end, unwind)))
                        throw new InvalidDataException("Invalid x64 function table");
                }
                _runtimeFunctions = functions;
            }
            if (!_runtimeFunctions.TryGetValue((uint)rva, out var first) || UnwindRoot(first) != first.Begin)
                throw new NotSupportedException("Camera target is not an independently bounded native function root");
            ulong endRva = first.End;
            foreach (var function in _runtimeFunctions.Values)
            {
                if (function.Begin < rva || function.Begin - rva >= (ulong)maximumBytes) continue;
                if (UnwindRoot(function) == rva) endRva = Math.Max(endRva, function.End);
            }
            if (endRva <= rva || endRva - rva > (ulong)maximumBytes)
                throw new NotSupportedException("Native camera function exceeds the bounded verification range");
            return ReadRva(rva, checked((int)(endRva - rva)));
        }

        private uint UnwindRoot(RuntimeFunction function)
        {
            var seen = new HashSet<uint>();
            for (int depth = 0; depth < 16; depth++)
            {
                if (!seen.Add(function.Begin)) throw new InvalidDataException("Cyclic unwind chain");
                var header = ReadRva(function.Unwind, 4);
                int version = header[0] & 7, flags = header[0] >> 3;
                if (version != 1 && version != 2) throw new NotSupportedException("Unknown x64 unwind version");
                if ((flags & 4) == 0) return function.Begin;
                if ((flags & 3) != 0) throw new InvalidDataException("Invalid chained unwind flags");
                int chainOffset = (4 + header[2] * 2 + 3) & ~3;
                var chain = ReadRva(function.Unwind + (ulong)chainOffset, 12);
                uint begin = BitConverter.ToUInt32(chain, 0), end = BitConverter.ToUInt32(chain, 4);
                uint unwind = BitConverter.ToUInt32(chain, 8);
                if (!_runtimeFunctions.TryGetValue(begin, out function) || function.End != end || function.Unwind != unwind)
                    throw new InvalidDataException("Unwind chain does not reference its runtime function");
            }
            throw new InvalidDataException("Too many chained unwind entries");
        }
    }

    public sealed class ResolvedGameBuild
    {
        public ulong AssemblyBase { get; init; }
        public ulong UnityBase { get; init; }
        public uint AssemblySize { get; init; }
        public uint UnitySize { get; init; }
        public PeImage AssemblyImage { get; init; }
        public PeImage UnityImage { get; init; }
        public ulong CameraSetterSlot { get; private set; }
        public ulong CameraGetterSlot { get; private set; }
        public ulong CameraSetterTarget { get; init; }
        public ulong CameraGetterTarget { get; init; }
        public ulong FrameSetterTarget { get; init; }
        public ulong FrameGetterTarget { get; init; }
        public int StoredFovOffset { get; private set; }
        private int _nativePointerOffset;
        private List<GameBuildResolver.Forwarder> _forwarders;
        private bool _waitingLogged;

        /// <summary>Finds only a uniquely matched setter cache; retains it after our own hook is published.</summary>
        public bool TryResolveCameraSlots(TargetProcess proc, Action<string> log = null)
        {
            if (CameraSetterSlot != 0) return true;
            if (AssemblyImage == null) throw new InvalidOperationException("GameAssembly image is unavailable");
            _forwarders ??= GameBuildResolver.DiscoverForwarders(AssemblyImage);
            var grouped = new SortedDictionary<ulong, List<GameBuildResolver.Forwarder>>();
            foreach (var forwarder in _forwarders)
            {
                if (!grouped.TryGetValue(forwarder.SlotRva, out var entries))
                    grouped.Add(forwarder.SlotRva, entries = new());
                entries.Add(forwarder);
            }
            var slots = new List<ulong>(grouped.Keys);
            var setters = new List<ulong>();
            var getters = new List<ulong>();
            for (int start = 0; start < slots.Count;)
            {
                int finish = start + 1;
                ulong page = slots[start] & ~0xFFFUL;
                while (finish < slots.Count && (slots[finish] & ~0xFFFUL) == page) finish++;
                int size = checked((int)(slots[finish - 1] - slots[start] + 8));
                var snapshot = proc.Read(checked(AssemblyBase + slots[start]), size);
                if (snapshot == null)
                {
                    if (!_waitingLogged) log?.Invoke("等待游戏初始化相机缓存…");
                    _waitingLogged = true;
                    return false; // An incomplete scan cannot establish a unique match.
                }
                for (int i = start; i < finish; i++)
                {
                    ulong pointer = BitConverter.ToUInt64(snapshot, checked((int)(slots[i] - slots[start])));
                    if (pointer == CameraSetterTarget) setters.Add(slots[i]);
                    if (pointer == CameraGetterTarget) getters.Add(slots[i]);
                }
                start = finish;
            }
            if (setters.Count > 1 || getters.Count > 1)
                throw new NotSupportedException("多个 IL2CPP 缓存都匹配命名相机接口；为避免歧义拒绝写入");
            if (setters.Count == 0)
            {
                if (!_waitingLogged) log?.Invoke("等待游戏初始化相机接口…");
                _waitingLogged = true;
                return false;
            }
            ulong setterRva = setters[0], getterRva = getters.Count == 1 ? getters[0] : 0;
            if (getterRva != 0 && getterRva == setterRva)
                throw new InvalidDataException("相机 getter 与 setter 不能共用同一个指针槽");
            VerifyForwarder(proc, grouped[setterRva][0]);
            if (getterRva != 0) VerifyForwarder(proc, grouped[getterRva][0]);
            if (proc.ReadUInt64(checked(AssemblyBase + setterRva)) != CameraSetterTarget)
                return false; // Initialization/ownership changed after the grouped snapshot.
            if (getterRva != 0 && proc.ReadUInt64(checked(AssemblyBase + getterRva)) != CameraGetterTarget)
                return false;
            CameraSetterSlot = checked(AssemblyBase + setterRva);
            CameraGetterSlot = getterRva == 0 ? 0 : checked(AssemblyBase + getterRva);
            _waitingLogged = false;
            log?.Invoke($"已定位相机 setter 缓存：GameAssembly+0x{setterRva:X}");
            return true;
        }

        private void VerifyForwarder(TargetProcess proc, GameBuildResolver.Forwarder forwarder)
        {
            var live = proc.Read(checked(AssemblyBase + forwarder.EntryRva), 16);
            if (live == null || !live.AsSpan().SequenceEqual(forwarder.Code))
                throw new InvalidOperationException("匹配到的 IL2CPP 转发器与磁盘指令不一致");
        }

        public void VerifyCameraAccessors(TargetProcess proc)
        {
            if (CameraSetterSlot == 0 || proc.ReadUInt64(CameraSetterSlot) != CameraSetterTarget)
                throw new InvalidOperationException("Camera setter cache does not contain its independently named native target");
            if (CameraGetterSlot != 0 && proc.ReadUInt64(CameraGetterSlot) != CameraGetterTarget)
                throw new InvalidOperationException("Camera getter cache no longer matches its named target");
            if (CameraSetterTarget == CameraGetterTarget)
                throw new InvalidDataException("Camera setter and getter resolve to the same function");
            VerifyNativeCode(proc, CameraSetterTarget);
            VerifyNativeCode(proc, CameraGetterTarget);
            var setter = UnityImage.ReadRva(CameraSetterTarget - UnityBase, 32);
            var getter = UnityImage.ReadRva(CameraGetterTarget - UnityBase, 32);
            // Validate the entire object-resolution path. Only its encoded object displacement is variable.
            byte[] prefix = { 0x48,0x83,0xEC,0x28,0x48,0x85,0xC9,0x74,0x15,0x48,0x8B,0x41,0,
                0x48,0x85,0xC0,0x74,0x0C,0x48,0x8B,0xC8,0x48,0x83,0xC4,0x28,0xE9 };
            for (int i = 0; i < prefix.Length; i++)
                if (i != 12 && (setter[i] != prefix[i] || getter[i] != prefix[i]))
                    throw new NotSupportedException("相机接口的对象解析/异常处理结构与预期不符");
            int objectOffset = unchecked((sbyte)setter[12]);
            if (objectOffset <= 0 || objectOffset % 8 != 0 || getter[12] != setter[12])
                throw new NotSupportedException("相机 getter/setter 对对象指针成员的位置不一致");
            ulong nativeSetterRva = GameBuildResolver.RelativeTarget(CameraSetterTarget - UnityBase + 30, BitConverter.ToInt32(setter, 26));
            ulong nativeGetterRva = GameBuildResolver.RelativeTarget(CameraGetterTarget - UnityBase + 30, BitConverter.ToInt32(getter, 26));
            var setterBody = UnityImage.ReadFunctionBytes(nativeSetterRva);
            var getterBody = UnityImage.ReadFunctionBytes(nativeGetterRva);
            VerifyNativeCode(proc, checked(UnityBase + nativeSetterRva), setterBody.Length);
            VerifyNativeCode(proc, checked(UnityBase + nativeGetterRva), getterBody.Length);
            byte[] argument = { 0x0F,0x28,0xF1 }; // MOVAPS XMM6,XMM1
            byte[] cameraBase = { 0x48,0x8B,0xD9 }; // MOV RBX,RCX
            if (setterBody.AsSpan().IndexOf(argument) < 0 || setterBody.AsSpan().IndexOf(cameraBase) < 0
                || getterBody.AsSpan().IndexOf(cameraBase) < 0)
                throw new NotSupportedException("相机参数/对象寄存器关系与预期不符");
            var fields = CameraFields(getterBody, 0x10);
            fields.IntersectWith(CameraFields(setterBody, 0x11));
            if (fields.Count != 1)
                throw new NotSupportedException("相机 getter/setter 未能确定唯一的视野字段");
            foreach (int field in fields) StoredFovOffset = field;
            _nativePointerOffset = objectOffset;
        }

        private static HashSet<int> CameraFields(byte[] body, byte operation)
        {
            var fields = new HashSet<int>();
            for (int i = 0; i + 8 <= body.Length; i++)
            {
                if (body[i] != 0xF3 || body[i + 1] != 0x0F || body[i + 2] != operation || body[i + 3] != 0x83) continue;
                int offset = BitConverter.ToInt32(body, i + 4);
                if (offset > 0 && offset <= 0x10000 && offset % 4 == 0) fields.Add(offset);
            }
            return fields;
        }

        public float? ReadStoredCameraFov(TargetProcess proc, ulong camera)
        {
            if (camera == 0 || StoredFovOffset == 0 || _nativePointerOffset == 0
                || camera > ulong.MaxValue - (ulong)_nativePointerOffset) return null;
            var pointer = proc.Read(camera + (ulong)_nativePointerOffset, 8);
            if (pointer == null) return null;
            ulong native = BitConverter.ToUInt64(pointer, 0);
            if (native == 0 || native > ulong.MaxValue - (ulong)StoredFovOffset) return null;
            var value = proc.Read(native + (ulong)StoredFovOffset, 4);
            return value == null ? null : BitConverter.ToSingle(value, 0);
        }

        public bool ContainsUnityCode(ulong address) => address >= UnityBase && UnityImage != null
            && UnityImage.IsExecutableRva(address - UnityBase);

        public void VerifyNativeCode(TargetProcess proc, ulong address, int length = 32)
        {
            if (length < 1 || address < UnityBase || UnityImage == null
                || !UnityImage.IsExecutableRva(address - UnityBase, length))
                throw new InvalidOperationException("Native accessor is outside file-backed executable UnityPlayer sections");
            var expected = UnityImage.ReadRva(address - UnityBase, length);
            var actual = proc.Read(address, length);
            if (actual == null || !actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Native accessor differs from its disk instructions; refusing an unknown patch");
        }
    }

    public static class GameBuildResolver
    {
        private static readonly string[] Names =
        {
            "UnityEngine.Camera::set_fieldOfView", "UnityEngine.Camera::get_fieldOfView",
            "UnityEngine.Application::set_targetFrameRate", "UnityEngine.Application::get_targetFrameRate",
        };
        private static readonly byte[] GetterPadding = { 0x66,0x0F,0x1F,0x84,0,0,0,0,0 };
        private static readonly byte[] SetterPadding = { 0x66,0x0F,0x1F,0x44,0,0 };
        internal sealed record Forwarder(ulong EntryRva, ulong SlotRva, byte[] Code);
        private sealed record Registration(ulong EntryRva, ulong LoopRva, ulong KeysRva, byte[] Entry,
            byte[] Loop, byte[] Keys, Dictionary<string, (ulong Rva, ulong TableEntry)> Targets);

        internal static ulong RelativeTarget(ulong nextRva, int displacement)
        {
            long target = checked((long)nextRva + displacement);
            if (target < 0 || target > uint.MaxValue) throw new InvalidDataException("Relative target is outside PE RVA space");
            return (ulong)target;
        }

        private static uint NameHash(string name)
        {
            uint hash = 2166136261;
            foreach (byte value in Encoding.ASCII.GetBytes(name)) hash = unchecked((hash ^ value) * 16777619);
            return hash;
        }

        private static bool RegistrationShape(ReadOnlySpan<byte> code) => code.Length >= 25
            && code[0] == 0x41 && code[1] == 0xB8
            && code[6] == 0x48 && code[7] == 0x8D && code[8] == 0x15
            && code[13] == 0x48 && code[14] == 0x8D && code[15] == 0x0D && code[20] == 0xE9;

        private static bool RegistrationLoop(PeImage image, ulong loopRva, byte[] loop)
        {
            byte[] saveFunctions = { 0x48,0x8B,0xF2 }, saveKeys = { 0x48,0x8B,0xF9 };
            byte[] count = { 0x49,0x63,0xE8 };
            if (loop.AsSpan().IndexOf(saveFunctions) < 0 || loop.AsSpan().IndexOf(saveKeys) < 0
                || loop.AsSpan().IndexOf(count) < 0) return false;
            byte[] pair = { 0x8B,0x0F,0x48,0x8B,0x14,0xDE,0xE8 };
            byte[] advance = { 0x48,0xFF,0xC3,0x48,0x8D,0x7F,0x04,0x48,0x3B,0xDD,0x7C,0xE9 };
            int matches = 0;
            for (int i = 0; i + 23 <= loop.Length; i++)
            {
                if (!loop.AsSpan(i, 7).SequenceEqual(pair) || !loop.AsSpan(i + 11, 12).SequenceEqual(advance)) continue;
                ulong helper = RelativeTarget(loopRva + (ulong)i + 11, BitConverter.ToInt32(loop, i + 7));
                if (!image.IsExecutableRva(helper, 16)) return false;
                matches++;
            }
            return matches == 1;
        }

        private static Registration ParseRegistration(PeImage image, ulong rva, byte[] entry)
        {
            int count = BitConverter.ToInt32(entry, 2);
            if (count < Names.Length || count > 10000) return null;
            ulong functionsRva = RelativeTarget(rva + 13, BitConverter.ToInt32(entry, 9));
            ulong keysRva = RelativeTarget(rva + 20, BitConverter.ToInt32(entry, 16));
            ulong loopRva = RelativeTarget(rva + 25, BitConverter.ToInt32(entry, 21));
            if (functionsRva % 8 != 0 || keysRva % 4 != 0
                || !image.IsFileBackedRva(functionsRva, count * 8) || !image.IsFileBackedRva(keysRva, count * 4)
                || !image.IsExecutableRva(loopRva, 144)) return null;
            byte[] loop = image.ReadRva(loopRva, 144);
            if (!RegistrationLoop(image, loopRva, loop)) return null;
            byte[] keys = image.ReadRva(keysRva, count * 4), functions = image.ReadRva(functionsRva, count * 8);
            var nativeRvas = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                ulong pointer = BitConverter.ToUInt64(functions, i * 8);
                if (pointer < image.PreferredImageBase) return null;
                ulong native = pointer - image.PreferredImageBase;
                if (!image.IsExecutableRva(native, 1)) return null;
                nativeRvas[i] = native;
            }
            var targets = new Dictionary<string, (ulong Rva, ulong TableEntry)>();
            var uniqueTargets = new HashSet<ulong>();
            foreach (string name in Names)
            {
                uint hash = NameHash(name);
                int found = -1;
                for (int i = 0; i < count; i++)
                    if (BitConverter.ToUInt32(keys, i * 4) == hash)
                    {
                        if (found >= 0) return null;
                        found = i;
                    }
                if (found < 0 || !image.IsExecutableRva(nativeRvas[found], 32)
                    || !uniqueTargets.Add(nativeRvas[found])) return null;
                targets.Add(name, (nativeRvas[found], functionsRva + (ulong)found * 8));
            }
            return new(rva, loopRva, keysRva, entry, loop, keys, targets);
        }

        private static Registration FindRegistration(PeImage image)
        {
            Registration found = null;
            foreach (var chunk in image.ReadExecutableChunks())
            {
                for (int i = 0; i < chunk.ScanLength && i + 25 <= chunk.Bytes.Length; i++)
                {
                    if (chunk.Bytes[i] != 0x41 || chunk.Bytes[i + 1] != 0xB8
                        || !RegistrationShape(chunk.Bytes.AsSpan(i, 25))) continue;
                    Registration candidate;
                    try { candidate = ParseRegistration(image, chunk.Rva + (ulong)i, chunk.Bytes.AsSpan(i, 25).ToArray()); }
                    catch (InvalidDataException) { continue; }
                    catch (OverflowException) { continue; }
                    if (candidate == null) continue;
                    if (found != null) throw new NotSupportedException("存在多个有效的内部调用注册结构，地址不唯一，已拒绝写入");
                    found = candidate;
                }
            }
            return found ?? throw new NotSupportedException("未找到唯一的内部调用注册结构；该客户端版本可能不受支持");
        }

        internal sealed record NamedRegistration(ulong TableEntryRva, ulong NativeTargetRva,
            ulong EntryRva, byte[] Entry, ulong LoopRva, byte[] Loop, ulong KeysRva, byte[] Keys);

        internal static NamedRegistration ResolveNamedRegistration(PeImage image, string name)
        {
            var registration = FindRegistration(image);
            uint hash = NameHash(name);
            int found = -1;
            for (int i = 0; i < registration.Keys.Length / 4; i++)
                if (BitConverter.ToUInt32(registration.Keys, i * 4) == hash)
                {
                    if (found >= 0) throw new NotSupportedException("命名内部调用存在重复键，已拒绝写入: " + name);
                    found = i;
                }
            if (found < 0) throw new NotSupportedException("缺少命名内部调用: " + name);
            ulong functions = RelativeTarget(registration.EntryRva + 13, BitConverter.ToInt32(registration.Entry, 9));
            ulong slot = checked(functions + (ulong)found * 8);
            ulong pointer = BitConverter.ToUInt64(image.ReadRva(slot, 8));
            if (pointer < image.PreferredImageBase || !image.IsExecutableRva(pointer - image.PreferredImageBase, 32))
                throw new InvalidDataException("命名内部调用不指向可执行目标: " + name);
            return new(slot, pointer - image.PreferredImageBase, registration.EntryRva, registration.Entry,
                registration.LoopRva, registration.Loop, registration.KeysRva, registration.Keys);
        }

        public static Dictionary<string, (ulong Rva, ulong TableEntry)> ResolveNativeRegistrations(PeImage image) =>
            FindRegistration(image).Targets;
        private static bool ForwarderShape(ReadOnlySpan<byte> code) => code.Length >= 16 && code[0] == 0x48
            && ((code[1] == 0xFF && code[2] == 0x25 && code.Slice(7, 9).SequenceEqual(GetterPadding))
                || (code[1] == 0x8B && code[2] == 0x05 && code[7] == 0x48 && code[8] == 0xFF
                    && code[9] == 0xE0 && code.Slice(10, 6).SequenceEqual(SetterPadding)));

        public static ulong DecodeSlotRva(ReadOnlySpan<byte> code, ulong entryRva)
        {
            if (!ForwarderShape(code)) throw new InvalidDataException("Unsupported padded IL2CPP forwarder");
            return RelativeTarget(checked(entryRva + 7), BitConverter.ToInt32(code.Slice(3, 4)));
        }

        internal static List<Forwarder> DiscoverForwarders(PeImage image)
        {
            var result = new List<Forwarder>();
            foreach (var chunk in image.ReadExecutableChunks())
            {
                int first = (int)((16 - (chunk.Rva & 15)) & 15);
                for (int i = first; i < chunk.ScanLength && i + 16 <= chunk.Bytes.Length; i += 16)
                {
                    if (chunk.Bytes[i] != 0x48 || !ForwarderShape(chunk.Bytes.AsSpan(i, 16))) continue;
                    ulong entry = chunk.Rva + (ulong)i, slot;
                    try { slot = DecodeSlotRva(chunk.Bytes.AsSpan(i, 16), entry); }
                    catch (InvalidDataException) { continue; }
                    if (slot % 8 != 0 || !image.IsWritableRva(slot, 8)) continue;
                    result.Add(new(entry, slot, chunk.Bytes.AsSpan(i, 16).ToArray()));
                    if (result.Count > 200000) throw new NotSupportedException("Too many IL2CPP forwarder candidates for bounded verification");
                }
            }
            if (result.Count == 0) throw new NotSupportedException("未找到受支持的 IL2CPP 指针缓存转发器");
            return result;
        }

        public static ResolvedGameBuild Resolve(TargetProcess proc, Action<string> log = null)
        {
            var assembly = TargetProcess.FindModule(proc.Pid, "GameAssembly.dll");
            var unity = TargetProcess.FindModule(proc.Pid, "UnityPlayer.dll");
            if (assembly.Base == 0 || unity.Base == 0)
                throw new InvalidOperationException("游戏尚未加载 GameAssembly.dll 与 UnityPlayer.dll");
            var assemblyImage = new PeImage(assembly.Path);
            var unityImage = new PeImage(unity.Path);
            if (assembly.Size != assemblyImage.SizeOfImage || unity.Size != unityImage.SizeOfImage)
                throw new InvalidOperationException("已加载模块大小与磁盘 PE 不一致");
            var registration = FindRegistration(unityImage);
            void VerifyRange(ulong rva, byte[] expected, string description)
            {
                var live = proc.Read(checked(unity.Base + rva), expected.Length);
                if (live == null || !live.AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException("Live " + description + " differs from the structurally resolved file");
            }
            VerifyRange(registration.EntryRva, registration.Entry, "internal-call registration entry");
            VerifyRange(registration.LoopRva, registration.Loop, "internal-call registration loop");
            VerifyRange(registration.KeysRva, registration.Keys, "internal-call name-key array");
            ulong Target(string name)
            {
                var native = registration.Targets[name];
                ulong target = checked(unity.Base + native.Rva);
                if (proc.ReadUInt64(checked(unity.Base + native.TableEntry)) != target)
                    throw new InvalidOperationException("Live native registration pointer changed: " + name);
                VerifyRange(native.Rva, unityImage.ReadRva(native.Rva, 32), "native accessor " + name);
                log?.Invoke($"已定位 {name} → UnityPlayer+0x{native.Rva:X}");
                return target;
            }
            var result = new ResolvedGameBuild
            {
                AssemblyBase = assembly.Base, AssemblySize = assembly.Size,
                UnityBase = unity.Base, UnitySize = unity.Size, AssemblyImage = assemblyImage, UnityImage = unityImage,
                CameraSetterTarget = Target(Names[0]), CameraGetterTarget = Target(Names[1]),
                FrameSetterTarget = Target(Names[2]), FrameGetterTarget = Target(Names[3]),
            };
            log?.Invoke($"已通过结构校验；注册入口位于 UnityPlayer+0x{registration.EntryRva:X}，相机缓存将在需要时解析");
            return result;
        }
    }
}

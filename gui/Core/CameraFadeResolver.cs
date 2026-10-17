using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace StarRailUnlocker.Core
{
    public sealed record CameraFadeFunction(ulong Rva, byte[] Code, ulong TableEntryRva, byte[] TableEntry,
        ulong UnwindRva, byte[] Unwind);
    public sealed record CameraFadeLiteral(ulong Rva, byte[] Bytes);

    /// <summary>A complete, unique native camera-transition cohort resolved from the current file.</summary>
    public sealed class ResolvedCameraFade
    {
        public CameraFadeFunction Distance { get; internal init; }
        public CameraFadeFunction Elevation { get; internal init; }
        public CameraFadeFunction Clear { get; internal init; }
        public CameraFadeFunction Ordinary { get; internal init; }
        public CameraFadeFunction Helper { get; internal init; }
        public int DistanceField { get; internal init; }
        public int ElevationField { get; internal init; }
        public int MaskField { get; internal init; }
        public IReadOnlyList<CameraFadeLiteral> Literals { get; internal init; }
        public IReadOnlyList<ulong> HotfixFlags { get; internal init; }
        internal byte[] DosHeader { get; init; }
        internal byte[] NtHeaders { get; init; }
        internal uint NtOffset { get; init; }
        internal string ImagePath { get; init; }
        internal uint Timestamp { get; init; }
        internal uint ImageSize { get; init; }
        public IEnumerable<CameraFadeFunction> Functions
        {
            get { yield return Distance; yield return Elevation; yield return Clear; yield return Ordinary; yield return Helper; }
        }
    }

    /// <summary>
    /// Streams semantic nuclei, then verifies complete bounded bodies, reciprocal scalar fields,
    /// common native calls, literal values and independent x64 unwind roots. No game address is fixed.
    /// Supported instruction shapes deliberately fail closed when a compiler or hotfix changes them.
    /// </summary>
    public static class CameraFadeResolver
    {
        private const int MaximumExceptionBytes = 32 * 1024 * 1024;
        internal sealed class BodyShape
        {
            private const int MaximumBodyBytes = 4096;
            private readonly byte[] _sha256;
            private readonly (int Offset, int Length)[] _wildcards;
            internal int Length { get; }
            internal BodyShape(int length, string sha256, params (int Offset, int Length)[] wildcards)
            {
                if (length < 1 || length > MaximumBodyBytes) throw new ArgumentOutOfRangeException(nameof(length));
                ArgumentNullException.ThrowIfNull(sha256);
                ArgumentNullException.ThrowIfNull(wildcards);
                if (sha256.Length != 64) throw new ArgumentException("A SHA-256 body fingerprint is required", nameof(sha256));
                try { _sha256 = Convert.FromHexString(sha256); }
                catch (FormatException ex) { throw new ArgumentException("Invalid SHA-256 body fingerprint", nameof(sha256), ex); }
                Length = length;
                _wildcards = ((int Offset, int Length)[])wildcards.Clone();
                int end = 0, masked = 0;
                foreach (var range in _wildcards)
                {
                    if (range.Offset < end || range.Length < 1 || range.Offset > Length || range.Length > Length - range.Offset)
                        throw new ArgumentException("Body wildcard ranges must be ordered, nonoverlapping and bounded", nameof(wildcards));
                    end = range.Offset + range.Length;
                    masked += range.Length;
                }
                if (masked == Length) throw new ArgumentException("A body fingerprint must retain fixed bytes", nameof(wildcards));
            }
            internal bool Matches(byte[] code)
            {
                if (code == null || code.Length != Length) return false;
                var normalized = (byte[])code.Clone();
                foreach (var range in _wildcards) normalized.AsSpan(range.Offset, range.Length).Clear();
                return SHA256.HashData(normalized).AsSpan().SequenceEqual(_sha256);
            }
        }

        private sealed class Pattern
        {
            private readonly byte[] _bytes;
            private readonly bool[] _fixed;
            internal readonly byte[] Anchor;
            internal readonly int AnchorOffset;
            internal int Length => _bytes.Length;
            internal Pattern(string text)
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _bytes = new byte[parts.Length]; _fixed = new bool[parts.Length];
                int best = 0, bestStart = 0, run = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    _fixed[i] = parts[i] != "??";
                    if (_fixed[i])
                    {
                        _bytes[i] = Convert.ToByte(parts[i], 16); run++;
                        if (run > best) { best = run; bestStart = i + 1 - run; }
                    }
                    else run = 0;
                }
                if (best == 0) throw new ArgumentException("A signature requires a fixed anchor");
                AnchorOffset = bestStart; Anchor = _bytes.AsSpan(bestStart, best).ToArray();
            }
            internal bool Matches(ReadOnlySpan<byte> bytes)
            {
                if (bytes.Length < Length) return false;
                for (int i = 0; i < Length; i++) if (_fixed[i] && bytes[i] != _bytes[i]) return false;
                return true;
            }
            internal List<int> Find(ReadOnlySpan<byte> bytes, int scanLength = int.MaxValue)
            {
                var matches = new List<int>(); int from = 0;
                while (from <= bytes.Length - Anchor.Length)
                {
                    int hit = bytes.Slice(from).IndexOf(Anchor);
                    if (hit < 0) break;
                    hit += from; int start = hit - AnchorOffset; from = hit + 1;
                    if (start >= 0 && start < scanLength && start + Length <= bytes.Length && Matches(bytes.Slice(start)))
                        matches.Add(start);
                }
                return matches;
            }
        }

        // Fingerprints retain the full fixed-byte check without embedding complete function bodies.
        // Variable displacements and IDs are normalized, then validated against the other members.
        private static readonly BodyShape DistanceBody = new(153,
            "E1B3AED06681E91AF2976901A23BAD14EA5D4E3F6E8E59E93A4E260883BC468F",
            (22, 4), (31, 1), (46, 4), (58, 1), (63, 1), (77, 4),
            (87, 1), (105, 4), (110, 4), (143, 4), (148, 4));
        private static readonly BodyShape ElevationBody = new(144,
            "7DD39E267963B61049B0F5383A200D7532A5D01B425F5140017A39F53388A78F",
            (18, 4), (27, 1), (42, 4), (54, 1), (59, 1), (73, 4),
            (83, 1), (100, 4), (105, 4), (134, 4), (139, 4));
        private static readonly BodyShape ClearBody = new(177,
            "5ABD0A5FE17AC57A8E3EA5AB16F8ACF11DF1CE577FC0A80FC21ECD12BADB01C3",
            (10, 4), (19, 1), (29, 1), (35, 4), (43, 1), (46, 4), (57, 4), (79, 4), (84, 4),
            (89, 4), (110, 4), (115, 4), (120, 4), (138, 4), (155, 4), (167, 4), (172, 4));
        private static readonly BodyShape OrdinaryBody = new(139,
            "FF8434911D14C6715B2F9156687D39B9F4761E4B7FBD530FD1ED58DE9944140C",
            (26, 4), (35, 1), (50, 4), (80, 4), (85, 4), (90, 4), (116, 4), (134, 4));
        private static readonly BodyShape PriorityBody = new(727,
            "9B56BD3CB8F9FD5CC2B521C7399B638127C15502BA39FE1034C6045F20C2F757",
            (34, 4), (47, 4), (73, 4), (79, 1), (90, 1), (108, 2), (114, 1), (127, 1),
            (150, 1), (155, 1), (167, 4), (191, 1), (225, 1), (234, 4), (240, 4),
            (253, 4), (266, 4), (303, 4), (317, 4), (325, 1), (331, 4), (336, 4),
            (367, 4), (374, 4), (379, 4), (396, 4), (424, 4), (460, 4), (466, 4),
            (478, 4), (483, 4), (503, 4), (518, 1), (527, 4), (532, 4), (546, 4),
            (568, 4), (578, 4), (592, 4), (599, 1), (608, 4), (613, 4), (639, 4),
            (649, 4), (654, 4), (659, 4), (688, 4), (695, 1), (702, 4), (707, 4),
            (712, 4), (717, 4), (722, 4));
        private static readonly Pattern DistanceNucleus = new("F3 0F 11 4E ?? F3 0F 59 4E ?? 48 89 F1 41 B8 01 00 00 00 41 89 D9 E8 ?? ?? ?? ??");
        private static readonly Pattern ElevationNucleus = new("F3 0F 11 4E ?? F3 0F 59 4E ?? 48 89 F1 41 B8 01 00 00 00 41 B1 01 E8 ?? ?? ?? ??");
        private static readonly Pattern ClearNucleus = new("80 4E ?? 02 F2 0F 10 05 ?? ?? ?? ?? F2 0F 11 46 ??");
        private static readonly Pattern OrdinaryNucleus = new("F3 0F 5D C8 48 89 F9 41 89 F0 41 89 D9 0F 28 74 24 30 48 83 C4 40 5B 5F 5E E9 ?? ?? ?? ??");
        private static readonly Pattern PriorityNucleus = new("89 F9 83 E1 1F BA 01 00 00 00 D3 E2 0F 2E 3D ?? ?? ?? ?? 8B 5E ??");

        private sealed class FunctionTable
        {
            private readonly PeImage _image;
            private readonly byte[] _table;
            private readonly uint _rva;
            private readonly int _count;
            internal readonly byte[] Dos, Nt;
            internal readonly uint NtOffset;
            internal FunctionTable(PeImage image)
            {
                _image = image;
                using var stream = File.OpenRead(image.Path);
                Dos = new byte[64]; stream.ReadExactly(Dos);
                NtOffset = BitConverter.ToUInt32(Dos, 60);
                if (BitConverter.ToUInt16(Dos, 0) != 0x5A4D || NtOffset < 64 || NtOffset > 1024 * 1024
                    || (ulong)NtOffset + 24 > (ulong)stream.Length) throw new InvalidDataException("Invalid DOS header");
                stream.Position = NtOffset; var fixedHeader = new byte[24]; stream.ReadExactly(fixedHeader);
                int sectionCount = BitConverter.ToUInt16(fixedHeader, 6), optionalSize = BitConverter.ToUInt16(fixedHeader, 20);
                if (BitConverter.ToUInt32(fixedHeader, 0) != 0x4550 || BitConverter.ToUInt16(fixedHeader, 4) != 0x8664
                    || sectionCount < 1 || sectionCount > 96 || optionalSize < 144 || optionalSize > 4096)
                    throw new InvalidDataException("Unsupported x64 header");
                Nt = new byte[checked(24 + optionalSize + sectionCount * 40)];
                stream.Position = NtOffset; stream.ReadExactly(Nt);
                if (BitConverter.ToUInt16(Nt, 24) != 0x20B || BitConverter.ToUInt32(Nt, 24 + 108) < 4
                    || BitConverter.ToUInt32(Nt, 8) != image.Timestamp || BitConverter.ToUInt32(Nt, 24 + 56) != image.SizeOfImage)
                    throw new InvalidDataException("PE identity changed while resolving camera transitions");
                int directory = 24 + 112 + 3 * 8;
                _rva = BitConverter.ToUInt32(Nt, directory); uint size = BitConverter.ToUInt32(Nt, directory + 4);
                if (size < 12 || size > MaximumExceptionBytes || size % 12 != 0)
                    throw new NotSupportedException("A bounded x64 exception table is required");
                RequireRange(image, _rva, checked((int)size));
                PeSection section = SectionFor(image, _rva, checked((int)size));
                stream.Position = checked((long)section.Raw + _rva - section.Rva);
                _table = new byte[checked((int)size)]; stream.ReadExactly(_table);
                int count = _table.Length / 12; uint previousEnd = 0; bool padding = false;
                for (int i = 0; i < count; i++)
                {
                    uint begin = U32(i, 0), end = U32(i, 4), unwind = U32(i, 8);
                    if ((begin | end | unwind) == 0) { padding = true; continue; }
                    if (padding || begin < previousEnd || end <= begin || end > image.SizeOfImage || unwind >= image.SizeOfImage)
                        throw new InvalidDataException("Unsorted or overlapping native function table");
                    previousEnd = end; _count++;
                }
                if (_count == 0) throw new InvalidDataException("Empty native function table");
            }
            private uint U32(int index, int offset) => BitConverter.ToUInt32(_table, checked(index * 12 + offset));
            internal CameraFadeFunction TryRead(ulong site, BodyShape body)
            {
                int low = 0, high = _count - 1, found = -1;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2;
                    if (U32(mid, 0) <= site) { found = mid; low = mid + 1; } else high = mid - 1;
                }
                if (found < 0 || site >= U32(found, 4)) return null;
                uint start = U32(found, 0), end = U32(found, 4), unwind = U32(found, 8);
                if (end - start != body.Length || !_image.IsExecutableRva(start, body.Length)) return null;
                byte[] code = _image.ReadRva(start, body.Length);
                if (!body.Matches(code)) return null;
                RequireRange(_image, unwind, 4); byte[] header = _image.ReadRva(unwind, 4);
                int version = header[0] & 7, flags = header[0] >> 3;
                if ((version != 1 && version != 2) || (flags & ~3) != 0 || header[1] > code.Length)
                    throw new NotSupportedException("Camera member is not an independent native unwind root");
                int unwindSize = (4 + header[2] * 2 + 3) & ~3;
                if ((flags & 3) != 0) unwindSize += 4;
                RequireRange(_image, unwind, unwindSize);
                return new(start, code, _rva + checked((uint)found * 12), _table.AsSpan(found * 12, 12).ToArray(),
                    unwind, _image.ReadRva(unwind, unwindSize));
            }
        }

        public static ResolvedCameraFade Resolve(PeImage image, Action<string> log = null)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var table = new FunctionTable(image);
            var nuclei = new[] { DistanceNucleus, ElevationNucleus, ClearNucleus, OrdinaryNucleus, PriorityNucleus };
            var bodies = new[] { DistanceBody, ElevationBody, ClearBody, OrdinaryBody, PriorityBody };
            var groups = new Dictionary<ulong, CameraFadeFunction>[5];
            for (int i = 0; i < groups.Length; i++) groups[i] = new();
            foreach (var chunk in image.ReadExecutableChunks(overlap: 64))
            {
                for (int k = 0; k < nuclei.Length; k++)
                {
                    foreach (int offset in nuclei[k].Find(chunk.Bytes, chunk.ScanLength))
                    {
                        var function = table.TryRead(chunk.Rva + (ulong)offset, bodies[k]);
                        if (function != null) groups[k].TryAdd(function.Rva, function);
                        if (groups[k].Count > 1) throw new NotSupportedException("Ambiguous native camera-transition cohort");
                    }
                }
            }
            var members = new CameraFadeFunction[5];
            for (int k = 0; k < groups.Length; k++)
            {
                if (groups[k].Count != 1) throw new NotSupportedException("Complete native camera-transition cohort was not found");
                foreach (var member in groups[k].Values) members[k] = member;
            }
            var distinct = new HashSet<ulong>(); foreach (var member in members) distinct.Add(member.Rva);
            if (distinct.Count != members.Length) throw new InvalidDataException("Camera member functions overlap");
            var distance = members[0]; var elevation = members[1]; var clear = members[2]; var ordinary = members[3]; var helper = members[4];
            int d = Unique(DistanceNucleus, distance.Code), e = Unique(ElevationNucleus, elevation.Code);
            int c = Unique(ClearNucleus, clear.Code), o = Unique(OrdinaryNucleus, ordinary.Code), h = Unique(PriorityNucleus, helper.Code);
            int distanceField = distance.Code[d + 4], elevationField = distance.Code[d + 9], mask = clear.Code[c + 2];
            if (elevationField < 16 || elevationField % 4 != 0 || distanceField != elevationField + 4
                || elevation.Code[e + 4] != elevationField || elevation.Code[e + 9] != distanceField
                || clear.Code[c + 16] != elevationField || helper.Code[h + 21] != mask)
                throw new InvalidDataException("Camera scalars or priority mask do not agree");
            int enabled = distance.Code[Unique(new Pattern("80 7E ?? 00 74 39"), distance.Code) + 2];
            if (elevation.Code[Unique(new Pattern("80 7E ?? 00 74 39"), elevation.Code) + 2] != enabled
                || clear.Code[Unique(new Pattern("80 7E ?? 00 0F 84"), clear.Code) + 2] != enabled
                || ordinary.Code[Unique(new Pattern("80 7F ?? 00 74 51"), ordinary.Code) + 2] != enabled
                || distance.Code[Unique(new Pattern("C7 46 ?? 00 00 80 3F"), distance.Code) + 2] != distanceField
                || elevation.Code[Unique(new Pattern("C7 46 ?? 00 00 80 3F"), elevation.Code) + 2] != elevationField)
                throw new InvalidDataException("Camera enabled/reset fields do not agree");
            foreach (int at in new Pattern("89 5E ??").Find(helper.Code))
                if (helper.Code[at + 2] != mask) throw new InvalidDataException("Camera priority mask writes disagree");
            RequireCall(distance, d + 22, helper.Rva, 0xE8);
            RequireCall(elevation, e + 22, helper.Rva, 0xE8);
            int tail = Unique(new Pattern("48 89 F1 41 B8 01 00 00 00 41 B1 01 48 83 C4 30 5E E9 ?? ?? ?? ??"), clear.Code);
            RequireCall(clear, tail + 17, helper.Rva, 0xE9);
            RequireCall(ordinary, o + 25, helper.Rva, 0xE9);
            if ((helper.Rva & 7) != 0) throw new NotSupportedException("Camera helper entry is not aligned");
            var literals = new List<CameraFadeLiteral>();
            var loadOne = new Pattern("F3 0F 10 0D ?? ?? ?? ??");
            foreach (var member in new[] { distance, elevation, ordinary, clear })
            {
                int at = Unique(loadOne, member.Code);
                AddLiteral(image, literals, Relative(member.Rva + (ulong)at + 8, BitConverter.ToInt32(member.Code, at + 4)), 4);
            }
            AddLiteral(image, literals, Relative(clear.Rva + (ulong)c + 12, BitConverter.ToInt32(clear.Code, c + 8)), 8);
            AddLiteral(image, literals, Relative(helper.Rva + (ulong)h + 19, BitConverter.ToInt32(helper.Code, h + 15)), 4);
            var flags = new HashSet<ulong>(); var flagPattern = new Pattern("80 3D ?? ?? ?? ?? 00");
            foreach (var member in members)
                foreach (int at in flagPattern.Find(member.Code))
                {
                    ulong rva = Relative(member.Rva + (ulong)at + 7, BitConverter.ToInt32(member.Code, at + 2));
                    if (!image.IsWritableRva(rva, 1)) throw new InvalidDataException("Hotfix guard is outside writable image data");
                    flags.Add(rva);
                }
            if (flags.Count == 0) throw new InvalidDataException("Native hotfix guard was not established");
            log?.Invoke("已定位反虚化相机标量、普通过渡与共享优先级函数");
            return new ResolvedCameraFade { Distance = distance, Elevation = elevation, Clear = clear, Ordinary = ordinary, Helper = helper,
                DistanceField = distanceField, ElevationField = elevationField, MaskField = mask, Literals = literals.AsReadOnly(),
                HotfixFlags = new List<ulong>(flags).AsReadOnly(), DosHeader = table.Dos, NtHeaders = table.Nt, NtOffset = table.NtOffset,
                ImagePath = System.IO.Path.GetFullPath(image.Path), Timestamp = image.Timestamp, ImageSize = image.SizeOfImage };
        }

        public static void VerifyLive(TargetProcess process, ResolvedGameBuild build, ResolvedCameraFade fade)
        {
            if (process == null || build?.AssemblyImage == null || fade == null) throw new ArgumentNullException(nameof(build));
            var module = TargetProcess.FindModule(process.Pid, "GameAssembly.dll");
            if (module.Base == 0 || module.Base != build.AssemblyBase || module.Size != build.AssemblySize
                || module.Size != fade.ImageSize || build.AssemblyImage.Timestamp != fade.Timestamp
                || !string.Equals(System.IO.Path.GetFullPath(module.Path), fade.ImagePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(System.IO.Path.GetFullPath(build.AssemblyImage.Path), fade.ImagePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GameAssembly module identity changed");
            var disk = new PeImage(fade.ImagePath);
            if (disk.Timestamp != fade.Timestamp || disk.SizeOfImage != fade.ImageSize)
                throw new InvalidOperationException("GameAssembly file changed after camera resolution");
            using (var stream = File.OpenRead(disk.Path))
            {
                byte[] dos = new byte[fade.DosHeader.Length]; stream.ReadExactly(dos);
                stream.Position = fade.NtOffset; byte[] nt = new byte[fade.NtHeaders.Length]; stream.ReadExactly(nt);
                if (!dos.AsSpan().SequenceEqual(fade.DosHeader) || !nt.AsSpan().SequenceEqual(fade.NtHeaders))
                    throw new InvalidOperationException("GameAssembly file headers changed after camera resolution");
            }
            Equal(process, build.AssemblyBase, fade.DosHeader);
            if (!MatchesMappedHeaders(fade.NtHeaders,
                process.Read(checked(build.AssemblyBase + fade.NtOffset), fade.NtHeaders.Length), build.AssemblyBase))
                throw new InvalidOperationException("GameAssembly mapped headers differ from their verified identity");
            void Range(ulong rva, byte[] expected)
            {
                if (!disk.ReadRva(rva, expected.Length).AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException("GameAssembly file changed after camera resolution");
                Equal(process, checked(build.AssemblyBase + rva), expected);
            }
            foreach (var member in fade.Functions)
            {
                Range(member.Rva, member.Code);
                Range(member.TableEntryRva, member.TableEntry);
                Range(member.UnwindRva, member.Unwind);
            }
            foreach (var literal in fade.Literals) Range(literal.Rva, literal.Bytes);
            foreach (ulong flag in fade.HotfixFlags)
            {
                byte[] live = process.Read(checked(build.AssemblyBase + flag), 1);
                if (live == null || live[0] != 0) throw new NotSupportedException("Camera transition is hotfixed or not initialized; retry after initialization");
            }
        }
        internal static void Equal(TargetProcess process, ulong address, byte[] expected)
        {
            byte[] actual = process.Read(address, expected.Length);
            if (actual == null || !actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Camera transition differs from its verified disk identity");
        }
        internal static bool MatchesMappedHeaders(byte[] expected, byte[] actual, ulong mappedBase)
        {
            const int imageBaseOffset = 48, afterImageBase = imageBaseOffset + 8;
            if (expected == null || actual == null || expected.Length < afterImageBase
                || actual.Length != expected.Length) return false;
            if (actual.AsSpan().SequenceEqual(expected)) return true;
            // Some loaders rewrite OptionalHeader.ImageBase after ASLR; every other byte stays exact.
            return BitConverter.ToUInt64(actual, imageBaseOffset) == mappedBase
                && actual.AsSpan(0, imageBaseOffset).SequenceEqual(expected.AsSpan(0, imageBaseOffset))
                && actual.AsSpan(afterImageBase).SequenceEqual(expected.AsSpan(afterImageBase));
        }
        private static int Unique(Pattern pattern, byte[] code)
        {
            var matches = pattern.Find(code);
            if (matches.Count != 1) throw new InvalidDataException("Ambiguous camera instruction relationship");
            return matches[0];
        }
        private static void RequireCall(CameraFadeFunction function, int offset, ulong target, byte opcode)
        {
            if (offset < 0 || offset > function.Code.Length - 5 || function.Code[offset] != opcode
                || Relative(function.Rva + (ulong)offset + 5, BitConverter.ToInt32(function.Code, offset + 1)) != target)
                throw new InvalidDataException("Camera transition does not call the common priority helper");
        }
        private static ulong Relative(ulong next, int displacement)
        {
            long rva = checked((long)next + displacement);
            if (rva <= 0 || rva > uint.MaxValue) throw new InvalidDataException("Native relative reference is outside RVA space");
            return (ulong)rva;
        }
        private static void AddLiteral(PeImage image, List<CameraFadeLiteral> literals, ulong rva, int size)
        {
            RequireRange(image, rva, size); byte[] bytes = image.ReadRva(rva, size);
            for (int at = 0; at < size; at += 4)
                if (BitConverter.ToUInt32(bytes, at) != 0x3F800000) throw new InvalidDataException("Camera unity-alpha literal differs");
            foreach (var prior in literals) if (prior.Rva == rva && prior.Bytes.Length == size) return;
            literals.Add(new(rva, bytes));
        }
        private static PeSection SectionFor(PeImage image, ulong rva, int size)
        {
            PeSection result = null; int overlaps = 0;
            foreach (var section in image.Sections)
            {
                ulong end = (ulong)section.Rva + section.RawSize;
                if (rva < end && section.Rva < rva + (ulong)size) overlaps++;
                if (rva >= section.Rva && rva + (ulong)size <= end) result = section;
            }
            if (overlaps != 1 || result == null) throw new InvalidDataException("Ambiguous file-backed PE range");
            return result;
        }
        private static void RequireRange(PeImage image, ulong rva, int size)
        {
            if (size <= 0 || rva == 0 || rva >= image.SizeOfImage || (ulong)size > image.SizeOfImage - rva)
                throw new InvalidDataException("Camera range is outside the image");
            SectionFor(image, rva, size);
        }
    }
}

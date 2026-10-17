using System.Text;
using StarRailUnlocker.Core;

/// <summary>
/// Portable structural tests for the dynamic PE resolver. Every image here is authored
/// byte-by-byte in this file; no game file, captured binary or product address is used.
/// The resolver must locate its targets from structure alone, so changing the synthetic
/// image base or the order of the tables must not change the resolved RVAs.
/// </summary>
internal static class SyntheticPeVerification
{
    private static readonly string[] Names =
    {
        "UnityEngine.Camera::set_fieldOfView", "UnityEngine.Camera::get_fieldOfView",
        "UnityEngine.Application::set_targetFrameRate", "UnityEngine.Application::get_targetFrameRate",
    };

    private const uint TextRva = 0x1000;
    private const uint TablesRva = 0x6000;
    private const uint SlotRva = 0x7000;

    private static uint NameHash(string name)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.ASCII.GetBytes(name)) hash = unchecked((hash ^ value) * 16777619);
        return hash;
    }

    private sealed class Layout
    {
        public byte[] Text = Array.Empty<byte>();
        public byte[] Tables = Array.Empty<byte>();
        public byte[] Data = Array.Empty<byte>();
        public int GetterForwarderOffset = 0x400;
        public int SetterForwarderOffset = 0x410;
        public int CameraSetterNativeOffset;
        public int CameraGetterNativeOffset;
        public int FrameSetterNativeOffset;
        public int FrameGetterNativeOffset;
        public uint GetterSlotRva = SlotRva;
        public uint SetterSlotRva = SlotRva + 8;
    }

    private static (byte[] Bytes, Layout Layout) BuildImage(ulong imageBase, bool duplicateRegistration, string omitName)
    {
        var layout = new Layout();
        var allNames = new List<string>(Names) { "Synthetic.Decoy::A", "Synthetic.Decoy::B", "Synthetic.Decoy::C", "Synthetic.Decoy::D" };
        if (omitName != null) allNames.Remove(omitName);
        int count = allNames.Count;

        const int loopOffset = 0x80, loopSize = 160, nativeStride = 0x30;
        int nativeStart = loopOffset + loopSize + 0x10;
        int[] nativeOffsets = new int[allNames.Count];
        for (int i = 0; i < nativeOffsets.Length; i++) nativeOffsets[i] = nativeStart + i * nativeStride;
        int OffsetOf(string name) { int index = allNames.IndexOf(name); return index < 0 ? 0 : nativeOffsets[index]; }
        layout.CameraSetterNativeOffset = OffsetOf(Names[0]);
        layout.CameraGetterNativeOffset = OffsetOf(Names[1]);
        layout.FrameSetterNativeOffset = OffsetOf(Names[2]);
        layout.FrameGetterNativeOffset = OffsetOf(Names[3]);
        int textSize = layout.SetterForwarderOffset + 0x20;
        var text = new List<byte>(new byte[textSize]);

        ulong FunctionsRva = TablesRva;
        ulong KeysRva = TablesRva + (ulong)(count * 8);

        void WriteEntry(int offset)
        {
            var entry = new List<byte> { 0x41, 0xB8 };
            entry.AddRange(BitConverter.GetBytes(count));
            entry.AddRange(new byte[] { 0x48, 0x8D, 0x15 });
            entry.AddRange(BitConverter.GetBytes(checked((int)((long)FunctionsRva - (long)(TextRva + (ulong)offset + 13)))));
            entry.AddRange(new byte[] { 0x48, 0x8D, 0x0D });
            entry.AddRange(BitConverter.GetBytes(checked((int)((long)KeysRva - (long)(TextRva + (ulong)offset + 20)))));
            entry.Add(0xE9);
            entry.AddRange(BitConverter.GetBytes(checked((int)((long)(TextRva + loopOffset) - (long)(TextRva + (ulong)offset + 25)))));
            for (int i = 0; i < entry.Count; i++) text[offset + i] = entry[i];
        }
        WriteEntry(0x00);
        if (duplicateRegistration) WriteEntry(0x20);

        // Loop body: three required markers plus exactly one helper call/advance pair.
        var loop = new byte[loopSize];
        Array.Fill(loop, (byte)0x90);
        new byte[] { 0x48, 0x8B, 0xF2 }.CopyTo(loop, 0);
        new byte[] { 0x48, 0x8B, 0xF9 }.CopyTo(loop, 3);
        new byte[] { 0x49, 0x63, 0xE8 }.CopyTo(loop, 6);
        const int pairOffset = 0x30, helperOffset = 0x2C0;
        new byte[] { 0x8B, 0x0F, 0x48, 0x8B, 0x14, 0xDE, 0xE8 }.CopyTo(loop, pairOffset);
        BitConverter.GetBytes(checked((int)(TextRva + helperOffset - (TextRva + loopOffset + pairOffset + 11))))
            .CopyTo(loop, pairOffset + 7);
        new byte[] { 0x48, 0xFF, 0xC3, 0x48, 0x8D, 0x7F, 0x04, 0x48, 0x3B, 0xDD, 0x7C, 0xE9 }.CopyTo(loop, pairOffset + 11);
        for (int i = 0; i < loop.Length; i++) text[loopOffset + i] = loop[i];

        // Helper reached by the loop pair: a single ret is enough to be executable and readable.
        text[helperOffset] = 0xC3;

        for (int i = 0; i < nativeOffsets.Length; i++)
        {
            int offset = nativeOffsets[i];
            text[offset] = 0x48; text[offset + 1] = 0x83; text[offset + 2] = 0xEC; text[offset + 3] = 0x28;
            for (int j = 4; j < nativeStride; j++) text[offset + j] = 0xCC;
        }

        // Two IL2CPP-style pointer-cache forwarders with adjacent writable slots.
        text[layout.GetterForwarderOffset] = 0x48; text[layout.GetterForwarderOffset + 1] = 0xFF;
        text[layout.GetterForwarderOffset + 2] = 0x25;
        var getterDisplacement = BitConverter.GetBytes(checked((int)(layout.GetterSlotRva - (TextRva + (ulong)layout.GetterForwarderOffset + 7))));
        for (int i = 0; i < 4; i++) text[layout.GetterForwarderOffset + 3 + i] = getterDisplacement[i];
        var getterPadding = new byte[] { 0x66, 0x0F, 0x1F, 0x84, 0, 0, 0, 0, 0 };
        for (int i = 0; i < getterPadding.Length; i++) text[layout.GetterForwarderOffset + 7 + i] = getterPadding[i];

        text[layout.SetterForwarderOffset] = 0x48; text[layout.SetterForwarderOffset + 1] = 0x8B;
        text[layout.SetterForwarderOffset + 2] = 0x05;
        var setterDisplacement = BitConverter.GetBytes(checked((int)(layout.SetterSlotRva - (TextRva + (ulong)layout.SetterForwarderOffset + 7))));
        for (int i = 0; i < 4; i++) text[layout.SetterForwarderOffset + 3 + i] = setterDisplacement[i];
        text[layout.SetterForwarderOffset + 7] = 0x48; text[layout.SetterForwarderOffset + 8] = 0xFF;
        text[layout.SetterForwarderOffset + 9] = 0xE0;
        var setterPadding = new byte[] { 0x66, 0x0F, 0x1F, 0x44, 0, 0 };
        for (int i = 0; i < setterPadding.Length; i++) text[layout.SetterForwarderOffset + 10 + i] = setterPadding[i];
        layout.Text = text.ToArray();

        var tables = new List<byte>(count * 12);
        for (int i = 0; i < allNames.Count; i++) tables.AddRange(BitConverter.GetBytes(imageBase + TextRva + (ulong)nativeOffsets[i]));
        for (int i = 0; i < allNames.Count; i++) tables.AddRange(BitConverter.GetBytes(NameHash(allNames[i])));
        layout.Tables = tables.ToArray();
        layout.Data = new byte[0x1000];
        return (Compose(imageBase, layout), layout);
    }

    private static byte[] Compose(ulong imageBase, Layout layout)
    {
        const int peOffset = 0x80, optionalSize = 0xF0, sectionCount = 3;
        int headersEnd = peOffset + 4 + 20 + optionalSize + sectionCount * 40;
        int textRaw = Align(headersEnd, 0x200);
        int tablesRaw = Align(textRaw + layout.Text.Length, 0x200);
        int dataRaw = Align(tablesRaw + layout.Tables.Length, 0x200);
        var image = new byte[dataRaw + layout.Data.Length];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(image, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, peOffset);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, peOffset + 4);
        BitConverter.GetBytes((ushort)sectionCount).CopyTo(image, peOffset + 6);
        BitConverter.GetBytes((ushort)optionalSize).CopyTo(image, peOffset + 20);
        BitConverter.GetBytes((ushort)0x0022).CopyTo(image, peOffset + 22);
        int optional = peOffset + 24;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, optional);
        BitConverter.GetBytes(imageBase).CopyTo(image, optional + 24);
        BitConverter.GetBytes((uint)(SlotRva + (uint)layout.Data.Length)).CopyTo(image, optional + 56);
        BitConverter.GetBytes((uint)0).CopyTo(image, optional + 108);
        WriteSection(image, optional + optionalSize, ".text", TextRva, (uint)layout.Text.Length, textRaw, 0x60000020);
        WriteSection(image, optional + optionalSize + 40, ".rdata", TablesRva, (uint)layout.Tables.Length, tablesRaw, 0x40000040);
        WriteSection(image, optional + optionalSize + 80, ".data", SlotRva, (uint)layout.Data.Length, dataRaw, 0xC0000040);
        layout.Text.CopyTo(image, textRaw);
        layout.Tables.CopyTo(image, tablesRaw);
        layout.Data.CopyTo(image, dataRaw);
        return image;
    }

    private static void WriteSection(byte[] image, int offset, string name, uint rva, uint rawSize, int raw, uint flags)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(image, offset);
        BitConverter.GetBytes(rawSize).CopyTo(image, offset + 8);
        BitConverter.GetBytes(rva).CopyTo(image, offset + 12);
        BitConverter.GetBytes(rawSize).CopyTo(image, offset + 16);
        BitConverter.GetBytes((uint)raw).CopyTo(image, offset + 20);
        BitConverter.GetBytes(flags).CopyTo(image, offset + 36);
    }

    private static int Align(int value, int boundary) => (value + boundary - 1) / boundary * boundary;

    private static string WriteImage(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "HSRUnlocker-synthetic-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static void Run(Action<bool, string> check)
    {
        VerifyHeaderAndStreaming(check);
        VerifyRegistration(check);
        VerifyForwarders(check);
    }

    private static void VerifyHeaderAndStreaming(Action<bool, string> check)
    {
        var (bytes, layout) = BuildImage(0x180000000, duplicateRegistration: false, omitName: null);
        string path = WriteImage(bytes);
        try
        {
            var image = new PeImage(path);
            check(image.SizeOfImage == SlotRva + 0x1000 && image.PreferredImageBase == 0x180000000 && image.Sections.Count == 3,
                "synthetic PE64 headers parse with three sections and the authored image base");
            check(image.IsExecutableRva(TextRva, 4) && !image.IsExecutableRva(TablesRva, 4)
                && image.IsWritableRva(SlotRva, 8) && !image.IsWritableRva(TextRva, 8),
                "synthetic section flags separate executable text, read-only tables and writable data");
            check(image.IsFileBackedRva(TablesRva, layout.Tables.Length)
                && !image.IsFileBackedRva(TablesRva, layout.Tables.Length + 0x2000),
                "file-backed range checks bound the synthetic table section");
            var chunks = image.ReadExecutableChunks(chunkSize: 4096, overlap: 24).ToList();
            check(chunks.Count > 0 && chunks.All(c => c.Bytes.Length >= c.ScanLength)
                && chunks.Select(c => c.Rva).Distinct().Count() == chunks.Count
                && chunks.Last().Rva + (ulong)chunks.Last().ScanLength >= TextRva + (ulong)layout.Text.Length,
                "streaming scan covers the executable section in ordered chunks with tail overlap");
            bool rejectedChunk = false;
            try { _ = image.ReadExecutableChunks(chunkSize: 512).ToList(); }
            catch (ArgumentOutOfRangeException) { rejectedChunk = true; }
            check(rejectedChunk, "streaming scan refuses an out-of-range chunk size before reading");
            bool rejectedRange = false;
            try { _ = image.ReadRva(image.SizeOfImage, 4); }
            catch (InvalidDataException) { rejectedRange = true; }
            check(rejectedRange, "an RVA outside the synthetic image is refused instead of reading unrelated bytes");
        }
        finally { File.Delete(path); }

        string bad = Path.Combine(Path.GetTempPath(), "HSRUnlocker-synthetic-bad-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(bad, Encoding.ASCII.GetBytes("not a portable executable"));
            bool rejectedHeader = false;
            try { _ = new PeImage(bad); }
            catch (InvalidDataException) { rejectedHeader = true; }
            check(rejectedHeader, "non-PE input is refused by header validation");

            var (complete, _) = BuildImage(0x180000000, false, null);
            var shortened = new byte[complete.Length - 0x800];
            Array.Copy(complete, shortened, shortened.Length);
            File.WriteAllBytes(bad, shortened);
            bool rejectedSection = false;
            try { _ = new PeImage(bad); }
            catch (InvalidDataException) { rejectedSection = true; }
            check(rejectedSection, "a section pointing past the end of the file is refused");
        }
        finally { File.Delete(bad); }
    }

    private static void VerifyRegistration(Action<bool, string> check)
    {
        var (bytes, layout) = BuildImage(0x180000000, duplicateRegistration: false, omitName: null);
        string path = WriteImage(bytes);
        try
        {
            var image = new PeImage(path);
            var targets = GameBuildResolver.ResolveNativeRegistrations(image);
            check(targets.Count == Names.Length && Names.All(n => targets.ContainsKey(n)),
                "structural registration discovery resolves all four authored internal-call names");
            check(Names.Select(n => targets[n].Rva).Distinct().Count() == Names.Length
                && Names.All(n => image.IsExecutableRva(targets[n].Rva, 32)),
                "resolved synthetic native targets are distinct executable RVAs");
            check(targets[Names[0]].Rva == TextRva + (ulong)layout.CameraSetterNativeOffset
                && targets[Names[1]].Rva == TextRva + (ulong)layout.CameraGetterNativeOffset
                && targets[Names[2]].Rva == TextRva + (ulong)layout.FrameSetterNativeOffset
                && targets[Names[3]].Rva == TextRva + (ulong)layout.FrameGetterNativeOffset,
                "each name resolves to its authored position rather than any fixed address");
            check(targets[Names[0]].TableEntry % 8 == 0 && targets[Names[0]].TableEntry != targets[Names[1]].TableEntry,
                "each name carries its own aligned pointer-table entry");
        }
        finally { File.Delete(path); }

        var (relocated, _) = BuildImage(0x7FF700000000, duplicateRegistration: false, omitName: null);
        string relocatedPath = WriteImage(relocated);
        try
        {
            var image = new PeImage(relocatedPath);
            var targets = GameBuildResolver.ResolveNativeRegistrations(image);
            check(targets[Names[0]].Rva == TextRva + 0x130 && targets[Names[2]].Rva == TextRva + 0x190,
                "identical structure at a distant image base resolves the same RVAs");
        }
        finally { File.Delete(relocatedPath); }

        var (duplicate, _) = BuildImage(0x180000000, duplicateRegistration: true, omitName: null);
        string duplicatePath = WriteImage(duplicate);
        try
        {
            bool refused = false;
            try { GameBuildResolver.ResolveNativeRegistrations(new PeImage(duplicatePath)); }
            catch (NotSupportedException) { refused = true; }
            check(refused, "two structurally valid registrations are refused as ambiguous");
        }
        finally { File.Delete(duplicatePath); }

        var (missing, _) = BuildImage(0x180000000, duplicateRegistration: false, omitName: Names[3]);
        string missingPath = WriteImage(missing);
        try
        {
            bool refused = false;
            try { GameBuildResolver.ResolveNativeRegistrations(new PeImage(missingPath)); }
            catch (NotSupportedException) { refused = true; }
            check(refused, "a registration missing one required name is refused");
        }
        finally { File.Delete(missingPath); }
    }

    private static void VerifyForwarders(Action<bool, string> check)
    {
        var (bytes, layout) = BuildImage(0x180000000, duplicateRegistration: false, omitName: null);
        string path = WriteImage(bytes);
        try
        {
            var image = new PeImage(path);
            var forwarders = GameBuildResolver.DiscoverForwarders(image);
            check(forwarders.Count == 2,
                "forwarder discovery finds exactly the two authored IL2CPP pointer-cache sequences");
            var getter = forwarders.Single(f => f.EntryRva == TextRva + (ulong)layout.GetterForwarderOffset);
            var setter = forwarders.Single(f => f.EntryRva == TextRva + (ulong)layout.SetterForwarderOffset);
            check(getter.SlotRva == layout.GetterSlotRva && setter.SlotRva == layout.SetterSlotRva
                && setter.SlotRva == getter.SlotRva + 8,
                "getter/setter forwarders decode their adjacent writable pointer caches");
            check(GameBuildResolver.DecodeSlotRva(getter.Code, getter.EntryRva) == layout.GetterSlotRva
                && GameBuildResolver.DecodeSlotRva(setter.Code, setter.EntryRva) == layout.SetterSlotRva,
                "RIP-relative slot decoding matches the streaming discovery result");
            var corrupted = (byte[])getter.Code.Clone();
            corrupted[7] = 0x00;
            bool refused = false;
            try { GameBuildResolver.DecodeSlotRva(corrupted, getter.EntryRva); }
            catch (InvalidDataException) { refused = true; }
            check(refused, "a forwarder without its required padding is refused");
        }
        finally { File.Delete(path); }
    }
}

using System;
using System.IO;
using System.Text;
using StarRailUnlocker.Core;

/// <summary>Authored PE64 inputs only; these checks never load or execute an input image.</summary>
internal static class MobilePeVerification
{
    private sealed class Fixture
    {
        internal readonly byte[] Bytes = new byte[0x1000];
        internal readonly uint Text, Data;
        internal uint Iat => Data + 0x320;
        internal const int Optional = 0x98;

        internal Fixture(ulong imageBase = 0x180000000, uint relocation = 0)
        {
            Text = 0x1000 + relocation;
            Data = 0x3000 + relocation;
            U16(0, 0x5A4D); U32(0x3C, 0x80); U32(0x80, 0x4550);
            U16(0x84, 0x8664); U16(0x86, 2); U16(0x94, 0xF0); U16(0x96, 0x22);
            U16(Optional, 0x20B); U64(Optional + 24, imageBase);
            U32(Optional + 32, 0x1000); U32(Optional + 36, 0x200);
            U32(Optional + 56, Data + 0x1000); U32(Optional + 60, 0x200);
            U32(Optional + 108, 16);
            U32(Optional + 112, Data); U32(Optional + 116, 0x100);
            U32(Optional + 120, Data + 0x200); U32(Optional + 124, 40);
            Section(Optional + 0xF0, ".text", Text, 0x200, 0x200, 0x60000020);
            Section(Optional + 0xF0 + 40, ".rdata", Data, 0xC00, 0x400, 0x40000040);
            Bytes[0x200] = 0xC3;
            R32(Data + 12, Data + 0x180); R32(Data + 16, 1);
            R32(Data + 20, 1); R32(Data + 24, 1);
            R32(Data + 28, Data + 0x100); R32(Data + 32, Data + 0x110);
            R32(Data + 36, Data + 0x120);
            R32(Data + 0x100, Text); R32(Data + 0x110, Data + 0x130);
            Name(Data + 0x130, "UnityMain"); Name(Data + 0x180, "UnityPlayer.dll");
            R32(Data + 0x200, Data + 0x300); R32(Data + 0x20C, Data + 0x340);
            R32(Data + 0x210, Iat);
            R64(Data + 0x300, Data + 0x370); R64(Iat, Data + 0x370);
            Name(Data + 0x340, "UnityPlayer.dll"); Name(Data + 0x372, "UnityMain");
        }

        internal int Offset(uint rva) => checked(0x400 + (int)(rva - Data));
        internal void U16(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        internal void U32(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        internal void U64(int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
        internal void R32(uint rva, uint value) => U32(Offset(rva), value);
        internal void R64(uint rva, ulong value) => U64(Offset(rva), value);
        internal void Name(uint rva, string value) => Encoding.ASCII.GetBytes(value + "\0").CopyTo(Bytes, Offset(rva));
        private void Section(int offset, string name, uint rva, uint size, uint raw, uint flags)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(Bytes, offset);
            U32(offset + 8, size); U32(offset + 12, rva); U32(offset + 16, size);
            U32(offset + 20, raw); U32(offset + 36, flags);
        }
    }

    public static void Run(Action<bool, string> check)
    {
        foreach (var image in new[] { new Fixture(), new Fixture(0x7FF600000000, 0x4000) })
        {
            Verify(check, image, true, image.Iat, "named UnityMain import resolves after image-base and section-RVA relocation");
            Verify(check, image, false, image.Text, "named executable UnityMain export resolves after relocation");
        }
        var ordinal = new Fixture();
        ordinal.R64(ordinal.Data + 0x300, 0x8000000000000011);
        ordinal.R64(ordinal.Data + 0x308, ordinal.Data + 0x370);
        ordinal.R64(ordinal.Iat, 0x8000000000000011); ordinal.R64(ordinal.Iat + 8, ordinal.Data + 0x370);
        Verify(check, ordinal, true, ordinal.Iat + 8, "ordinal imports are skipped before the uniquely named import");
        var fallback = new Fixture(); fallback.R32(fallback.Data + 0x200, 0);
        Verify(check, fallback, true, fallback.Iat, "an unbound IAT supplies the name lookup when OriginalFirstThunk is absent");
        var moduleCase = new Fixture(); moduleCase.Name(moduleCase.Data + 0x340, "UNITYPLAYER.DLL");
        Verify(check, moduleCase, true, moduleCase.Iat, "DLL import names match without case sensitivity");

        Reject(check, true, "missing or differently cased named imports are refused", f => f.Name(f.Data + 0x372, "unityMain"));
        Reject(check, true, "duplicate UnityMain imports are ambiguous", f =>
        {
            f.R64(f.Data + 0x308, f.Data + 0x370); f.R64(f.Iat + 8, f.Data + 0x370);
        });
        Reject(check, true, "import descriptor termination is required", f => f.U32(Fixture.Optional + 124, 20));
        Reject(check, true, "partial import descriptors are refused", f => f.U32(Fixture.Optional + 124, 19));
        Reject(check, true, "import directory overflow is refused", f => f.U32(Fixture.Optional + 120, 0xFFFFFFF0));
        Reject(check, true, "unterminated thunk tables are refused", f =>
        {
            f.R32(f.Data + 0x200, f.Data + 0xBF8); f.R64(f.Data + 0xBF8, f.Data + 0x370);
        });
        Reject(check, true, "the IAT must include its corresponding terminator", f => f.R64(f.Iat + 8, f.Data + 0x370));
        Reject(check, true, "import names require a terminator within 256 ASCII bytes", f =>
        {
            Array.Fill(f.Bytes, (byte)'A', f.Offset(f.Data + 0x372), 256);
        });
        Reject(check, true, "ordinal imports with reserved high bits are refused", f => f.R64(f.Data + 0x300, 0x8000000000010011));

        Reject(check, false, "missing named exports are refused", f => f.Name(f.Data + 0x130, "UnityMaim"));
        Reject(check, false, "duplicate UnityMain export names are ambiguous", f =>
        {
            f.R32(f.Data + 24, 2); f.R32(f.Data + 0x114, f.Data + 0x130);
        });
        Reject(check, false, "forwarded UnityMain exports are refused", f => f.R32(f.Data + 0x100, f.Data + 0x70));
        Reject(check, false, "nonexecutable UnityMain exports are refused", f => f.R32(f.Data + 0x100, f.Data + 0x200));
        Reject(check, false, "export ordinals must index the address table", f => f.U16(f.Offset(f.Data + 0x120), 1));
        Reject(check, false, "oversized export address tables are refused", f => f.R32(f.Data + 20, uint.MaxValue));
        Reject(check, false, "export name table ranges must be file backed", f => f.R32(f.Data + 32, f.Data + 0xFFF));
        Reject(check, false, "truncated export directories are refused", f => f.U32(Fixture.Optional + 116, 39));
        Reject(check, false, "x86 images cannot supply an x64 entry point", f => f.U16(0x84, 0x14C));
        var truncated = new Fixture();
        Verify(check, truncated, false, null, "a section truncated on disk is refused", truncated.Bytes.Length - 1);
    }

    private static void Reject(Action<bool, string> check, bool import, string message, Action<Fixture> corrupt)
    {
        var image = new Fixture();
        corrupt(image);
        Verify(check, image, import, null, message);
    }

    private static void Verify(Action<bool, string> check, Fixture image, bool import, uint? expected,
        string message, int? fileLength = null)
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HSRUnlocker-mobile-pe-" + Guid.NewGuid().ToString("N") + ".bin"));
        try
        {
            File.WriteAllBytes(path, image.Bytes.AsSpan(0, fileLength ?? image.Bytes.Length).ToArray());
            uint actual;
            try
            {
                actual = import ? MobileEntryResolver.FindUnityMainImport(path) : MobileEntryResolver.FindUnityMainExport(path);
            }
            catch (InvalidDataException)
            {
                check(expected == null, message);
                return;
            }
            check(expected.HasValue && actual == expected.Value, message);
        }
        finally { File.Delete(path); }
    }
}

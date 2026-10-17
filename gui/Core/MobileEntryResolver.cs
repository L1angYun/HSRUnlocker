using System;
using System.IO;
using System.Text;

namespace StarRailUnlocker.Core
{
    /// <summary>Resolves the named engine entry from disk without loading or executing either image.</summary>
    public static class MobileEntryResolver
    {
        private const uint MaximumTableBytes = 8 * 1024 * 1024;
        private const int MaximumDescriptors = 4096;
        private const int MaximumNameBytes = 256;
        private const ulong OrdinalFlag64 = 0x8000000000000000UL;

        /// <summary>Returns the uniquely named UnityPlayer.dll!UnityMain IAT slot RVA.</summary>
        public static uint FindUnityMainImport(string executable)
        {
            var image = new PeImage(executable);
            var directory = ReadDirectory(image, 1);
            var descriptors = ReadTable(image, directory.Rva, directory.Size);
            uint result = 0;
            int matches = 0;
            bool terminated = false;
            for (int offset = 0, count = 0; offset <= descriptors.Length - 20; offset += 20)
            {
                if (++count > MaximumDescriptors)
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.ImportDescriptorLimitExceeded"));
                uint lookup = BitConverter.ToUInt32(descriptors, offset);
                uint timestamp = BitConverter.ToUInt32(descriptors, offset + 4);
                uint chain = BitConverter.ToUInt32(descriptors, offset + 8);
                uint name = BitConverter.ToUInt32(descriptors, offset + 12);
                uint iat = BitConverter.ToUInt32(descriptors, offset + 16);
                if ((lookup | timestamp | chain | name | iat) == 0)
                {
                    terminated = true;
                    break;
                }
                if (name == 0 || iat == 0)
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.IncompleteImportDescriptor"));
                string module = ReadAscii(image, name);
                RequireRange(image, iat, 8);
                if (lookup != 0) RequireRange(image, lookup, 8);
                if (!string.Equals(module, "UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) continue;

                uint list = lookup == 0 ? iat : lookup;
                uint available = FileBackedAvailable(image, list);
                uint length = Math.Min(available, MaximumTableBytes) & ~7U;
                if (length < 8) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.TruncatedImportThunkTable"));
                var thunks = ReadTable(image, list, length);
                bool thunkTerminated = false;
                for (int index = 0; index < thunks.Length / 8; index++)
                {
                    ulong thunk = BitConverter.ToUInt64(thunks, index * 8);
                    ulong slot = (ulong)iat + (uint)index * 8UL;
                    RequireRange(image, slot, 8);
                    if (thunk == 0)
                    {
                        if (BitConverter.ToUInt64(image.ReadRva(slot, 8), 0) != 0)
                            throw new InvalidDataException(L10n.T("Resolver.MobileEntry.MissingIatTerminator"));
                        thunkTerminated = true;
                        break;
                    }
                    if ((thunk & OrdinalFlag64) != 0)
                    {
                        if ((thunk & 0x7FFFFFFFFFFF0000UL) != 0)
                            throw new InvalidDataException(L10n.T("Resolver.MobileEntry.InvalidOrdinalImport"));
                        continue;
                    }
                    if (thunk > uint.MaxValue)
                        throw new InvalidDataException(L10n.T("Resolver.MobileEntry.ImportNameNotImageRva"));
                    RequireRange(image, thunk, 3); // Hint followed by a terminated ASCII name.
                    string symbol = ReadAscii(image, thunk + 2);
                    if (!string.Equals(symbol, "UnityMain", StringComparison.Ordinal)) continue;
                    if (++matches != 1)
                        throw new InvalidDataException(L10n.T("Resolver.MobileEntry.AmbiguousUnityMainImport"));
                    result = checked((uint)slot);
                }
                if (!thunkTerminated)
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnterminatedOrOversizedImportThunkTable"));
            }
            if (!terminated) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnterminatedImportDescriptorTable"));
            if (matches != 1) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnityMainImportNotFound"));
            return result;
        }

        /// <summary>Returns the unique, executable, file-backed UnityMain export RVA; rejects forwarders.</summary>
        public static uint FindUnityMainExport(string unityPlayer)
        {
            var image = new PeImage(unityPlayer);
            var directory = ReadDirectory(image, 0);
            if (directory.Size < 40) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.TruncatedExportDirectory"));
            var header = image.ReadRva(directory.Rva, 40);
            uint functions = BitConverter.ToUInt32(header, 20);
            uint names = BitConverter.ToUInt32(header, 24);
            uint functionTable = BitConverter.ToUInt32(header, 28);
            uint nameTable = BitConverter.ToUInt32(header, 32);
            uint ordinalTable = BitConverter.ToUInt32(header, 36);
            if (functions == 0 || names == 0)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.NoNamedExports"));
            var addresses = ReadTable(image, functionTable, TableSize(functions, 4));
            var namePointers = ReadTable(image, nameTable, TableSize(names, 4));
            var ordinals = ReadTable(image, ordinalTable, TableSize(names, 2));
            uint result = 0;
            int matches = 0;
            for (int i = 0; i < namePointers.Length / 4; i++)
            {
                uint name = BitConverter.ToUInt32(namePointers, i * 4);
                ushort ordinal = BitConverter.ToUInt16(ordinals, i * 2);
                if (ordinal >= functions) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.ExportOrdinalOutsideAddressTable"));
                string symbol = ReadAscii(image, name);
                if (!string.Equals(symbol, "UnityMain", StringComparison.Ordinal)) continue;
                if (++matches != 1) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.AmbiguousUnityMainExport"));
                uint address = BitConverter.ToUInt32(addresses, ordinal * 4);
                if (address >= directory.Rva && (ulong)address - directory.Rva < directory.Size)
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnityMainForwardedExport"));
                RequireRange(image, address, 1);
                if (!image.IsExecutableRva(address))
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnityMainNotExecutableFileBackedCode"));
                result = address;
            }
            if (matches != 1) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnityMainExportNotFound"));
            return result;
        }

        private static (uint Rva, uint Size) ReadDirectory(PeImage image, int index)
        {
            using var stream = File.OpenRead(image.Path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            stream.Position = 0x3C;
            int pe = reader.ReadInt32();
            if (pe < 0x40 || pe > stream.Length - 24)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.InvalidPeHeaderOffset"));
            stream.Position = pe;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                throw new InvalidDataException(L10n.T("Resolver.Pe.ExpectedX64"));
            reader.ReadUInt16();
            if (reader.ReadUInt32() != image.Timestamp)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.PeMetadataChangedDuringResolution"));
            stream.Position = pe + 20L;
            ushort optionalSize = reader.ReadUInt16();
            long optional = pe + 24L;
            if (optionalSize < 112 + (index + 1) * 8 || optional + optionalSize > stream.Length)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.TruncatedPeOptionalHeader"));
            stream.Position = optional;
            if (reader.ReadUInt16() != 0x20B) throw new InvalidDataException(L10n.T("Resolver.Pe.ExpectedPe32Plus"));
            stream.Position = optional + 24;
            if (reader.ReadUInt64() != image.PreferredImageBase)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.PeImageBaseChangedDuringResolution"));
            stream.Position = optional + 56;
            if (reader.ReadUInt32() != image.SizeOfImage)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.PeImageSizeChangedDuringResolution"));
            stream.Position = optional + 108;
            uint count = reader.ReadUInt32();
            if (count <= index || (ulong)count * 8 > (uint)optionalSize - 112U)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.MissingOrInvalidPeDirectoryCount"));
            stream.Position = optional + 112 + index * 8;
            uint rva = reader.ReadUInt32(), size = reader.ReadUInt32();
            if (rva == 0 || size == 0 || size > MaximumTableBytes)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.MissingOrOversizedPeDirectory"));
            RequireRange(image, rva, size);
            return (rva, size);
        }

        private static uint TableSize(uint count, uint elementSize)
        {
            ulong size = (ulong)count * elementSize;
            if (size == 0 || size > MaximumTableBytes)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.InvalidOrOversizedPeTable"));
            return (uint)size;
        }

        private static byte[] ReadTable(PeImage image, uint rva, uint size)
        {
            if (size == 0 || size > MaximumTableBytes)
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.InvalidOrOversizedPeTable"));
            RequireRange(image, rva, size);
            var result = new byte[checked((int)size)];
            for (int offset = 0; offset < result.Length;)
            {
                int length = Math.Min(4 * 1024 * 1024, result.Length - offset);
                var part = image.ReadRva((ulong)rva + (uint)offset, length);
                Buffer.BlockCopy(part, 0, result, offset, length);
                offset += length;
            }
            return result;
        }

        private static string ReadAscii(PeImage image, ulong rva)
        {
            uint available = FileBackedAvailable(image, rva);
            int length = (int)Math.Min(available, MaximumNameBytes);
            var bytes = image.ReadRva(rva, length);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    if (i == 0) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.EmptyPeModuleOrSymbolName"));
                    RequireRange(image, rva, (uint)i + 1U);
                    return Encoding.ASCII.GetString(bytes, 0, i);
                }
                if (bytes[i] < 0x20 || bytes[i] > 0x7E)
                    throw new InvalidDataException(L10n.T("Resolver.MobileEntry.NonAsciiPeModuleOrSymbolName"));
            }
            throw new InvalidDataException(L10n.T("Resolver.MobileEntry.UnterminatedOrOversizedPeName"));
        }

        private static uint FileBackedAvailable(PeImage image, ulong rva)
        {
            RequireRange(image, rva, 1);
            foreach (var section in image.Sections)
            {
                if (rva >= section.Rva && rva - section.Rva < section.RawSize)
                    return checked((uint)Math.Min((ulong)section.RawSize - (rva - section.Rva), image.SizeOfImage - rva));
            }
            throw new InvalidDataException(L10n.T("Resolver.MobileEntry.PeRvaHasNoFileBackedSection"));
        }

        private static void RequireRange(PeImage image, ulong rva, ulong size)
        {
            if (rva == 0 || size == 0 || rva >= image.SizeOfImage || size > image.SizeOfImage - rva
                || size > int.MaxValue || !image.IsFileBackedRva(rva, (int)size))
                throw new InvalidDataException(L10n.T("Resolver.MobileEntry.PeRangeOutsideFileBackedImage"));
            int matches = 0;
            foreach (var section in image.Sections)
            {
                // Refuse overlapping section mappings even when just one contains the entire request.
                if (section.RawSize != 0 && rva < (ulong)section.Rva + section.RawSize
                    && (ulong)section.Rva < rva + size) matches++;
            }
            if (matches != 1) throw new InvalidDataException(L10n.T("Resolver.MobileEntry.AmbiguousPeSectionMapping"));
        }
    }
}

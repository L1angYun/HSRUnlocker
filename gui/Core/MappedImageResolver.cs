using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StarRailUnlocker.Core
{
    /// <summary>Finds one exact mapped image without relying on an initialized loader list.</summary>
    internal static class MappedImageResolver
    {
        private const uint MemImage = 0x1000000, MemReserve = 0x2000, PageGuard = 0x100;
        private const int MaximumRegions = 65536, MaximumNameLength = 32768;

        internal readonly record struct Region(ulong Base, ulong AllocationBase, ulong Size,
            uint State, uint Protection, uint Type);

        internal static (ulong Base, uint Size) Find(TargetProcess process, IntPtr localModule)
        {
            string expected = Filename(GetCurrentProcess(), unchecked((ulong)localModule.ToInt64()));
            GetNativeSystemInfo(out var limits);
            return Find(expected, unchecked((ulong)limits.MinimumApplicationAddress.ToInt64()),
                unchecked((ulong)limits.MaximumApplicationAddress.ToInt64()),
                address => Query(process.Handle, address), address => Filename(process.Handle, address));
        }

        internal static (ulong Base, uint Size) Find(string expectedPath, ulong minimumAddress,
            ulong maximumAddress, Func<ulong, Region> query, Func<ulong, string> fileName)
        {
            if (!ValidName(expectedPath) || minimumAddress > maximumAddress || maximumAddress > long.MaxValue
                || query == null || fileName == null)
                throw Unverifiable();
            ulong found = 0, imageEnd = 0, cursor = minimumAddress;
            bool completeImage = false;
            for (int count = 0; cursor <= maximumAddress; count++)
            {
                if (count >= MaximumRegions) throw Unverifiable();
                Region region = query(cursor);
                if (!TryEnd(region, cursor, out ulong end)) throw Unverifiable();
                if (region.Type == MemImage && region.State == Native.MEM_COMMIT
                    && region.Base == region.AllocationBase)
                {
                    string name = fileName(region.Base);
                    if (!ValidName(name)) throw Unverifiable();
                    if (string.Equals(name, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (found != 0 || region.Base == 0) throw Unverifiable();
                        found = region.Base;
                        imageEnd = region.Base;
                    }
                }
                if (found != 0 && region.AllocationBase == found)
                {
                    if (completeImage || region.Type != MemImage
                        || (region.State != Native.MEM_COMMIT && region.State != MemReserve)
                        || region.Base != imageEnd || end - found > uint.MaxValue)
                        throw Unverifiable();
                    imageEnd = end;
                }
                else if (found != 0) completeImage = true;
                cursor = end;
            }
            return found == 0 ? (0, 0) : (found, checked((uint)(imageEnd - found)));
        }

        internal static bool IsExecutableRange(IntPtr process, ulong imageBase, ulong address, int length) =>
            IsExecutableRange(imageBase, address, length, cursor => Query(process, cursor));

        internal static bool IsExecutableRange(ulong imageBase, ulong address, int length,
            Func<ulong, Region> query)
        {
            if (imageBase == 0 || address < imageBase || length < 1 || length > 4096
                || address > ulong.MaxValue - (ulong)length || query == null)
                return false;
            ulong end = address + (ulong)length, cursor = address;
            for (int count = 0; cursor < end; count++)
            {
                if (count >= 4096) return false;
                Region region = query(cursor);
                if (!TryEnd(region, cursor, out ulong regionEnd) || region.AllocationBase != imageBase
                    || region.Type != MemImage || region.State != Native.MEM_COMMIT
                    || (region.Protection & PageGuard) != 0
                    || (region.Protection & 0xFF) is not (0x10 or 0x20 or 0x40 or 0x80))
                    return false;
                cursor = Math.Min(end, regionEnd);
            }
            return true;
        }

        private static bool TryEnd(Region region, ulong cursor, out ulong end)
        {
            end = 0;
            if (region.Size == 0 || region.Base > cursor || region.Size > ulong.MaxValue - region.Base)
                return false;
            end = region.Base + region.Size;
            return end > cursor;
        }

        private static bool ValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length < MaximumNameLength;

        private static InvalidOperationException Unverifiable() =>
            new(L10n.T("Resolver.FunctionTable.CannotVerifyTargetModule"));

        private static Region Query(IntPtr process, ulong address)
        {
            if (VirtualQueryEx(process, (IntPtr)unchecked((long)address), out var memory,
                (UIntPtr)Marshal.SizeOf<MemoryInformation>()) == UIntPtr.Zero)
                throw Unverifiable();
            return new(unchecked((ulong)memory.BaseAddress.ToInt64()),
                unchecked((ulong)memory.AllocationBase.ToInt64()), memory.RegionSize.ToUInt64(),
                memory.State, memory.Protect, memory.Type);
        }

        private static string Filename(IntPtr process, ulong address)
        {
            var name = new StringBuilder(MaximumNameLength);
            uint length = K32GetMappedFileNameW(process, (IntPtr)unchecked((long)address), name, name.Capacity);
            if (length == 0 || length >= name.Capacity || length != name.Length) throw Unverifiable();
            return name.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInformation
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInformation
        {
            public ushort ProcessorArchitecture, Reserved;
            public uint PageSize;
            public IntPtr MinimumApplicationAddress, MaximumApplicationAddress;
            public UIntPtr ActiveProcessorMask;
            public uint NumberOfProcessors, ProcessorType, AllocationGranularity;
            public ushort ProcessorLevel, ProcessorRevision;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address,
            out MemoryInformation information, UIntPtr size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder name, int size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern void GetNativeSystemInfo(out SystemInformation information);
    }
}

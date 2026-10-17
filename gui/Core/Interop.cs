using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StarRailUnlocker.Core
{
    /// <summary>Win32 interop used by the unlocker.</summary>
    public static class Native
    {
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_VM_OPERATION = 0x0008;

        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;

        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_EXECUTE_READ = 0x20;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;

        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint TH32CS_SNAPMODULE = 0x00000008;
        public const uint TH32CS_SNAPMODULE32 = 0x00000010;

        /// <summary>
        /// PROCESSENTRY32W.  Keep th32DefaultHeapID as IntPtr: that makes the structure
        /// 568 bytes with szExeFile at offset 44, exactly like the native layout, and it
        /// is verified to enumerate processes correctly on x64.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MODULEENTRY32W
        {
            public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W me);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf,
                                                    IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf,
                                                     IntPtr size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size,
                                                   uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size,
                                                   uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetExitCodeProcess(IntPtr h, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize,
            IntPtr start, IntPtr parameter, uint flags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr handle, out uint code);
    }

    /// <summary>A process we can read from and write to.</summary>
    public sealed class TargetProcess : IDisposable
    {
        public uint Pid { get; }
        public IntPtr Handle { get; private set; }

        public TargetProcess(uint pid, bool readOnly = false)
        {
            Pid = pid;
            var access = Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ;
            if (!readOnly) access |= Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | 0x0002;
            Handle = Native.OpenProcess(access, false, pid);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "无法打开目标进程（错误 " + Marshal.GetLastWin32Error() +
                    "）。请以管理员身份运行 HSRUnlocker。");
        }

        // ------------------------------------------------------------------ memory
        public bool IsAlive => Handle != IntPtr.Zero && Native.GetExitCodeProcess(Handle, out var code) != 0 && code == 259;

        public byte[] Read(ulong address, int size)
        {
            var buf = new byte[size];
            if (!Native.ReadProcessMemory(Handle, (IntPtr)(long)address, buf, (IntPtr)size, out var read)
                || read.ToInt64() != size)
                return null;
            return buf;
        }

        public int ReadInt32(ulong address)
        {
            var b = Read(address, 4);
            return b == null ? int.MinValue : BitConverter.ToInt32(b, 0);
        }

        public ulong ReadUInt64(ulong address)
        {
            var b = Read(address, 8);
            if (b == null) throw new InvalidOperationException($"Cannot read pointer at 0x{address:X}");
            return BitConverter.ToUInt64(b, 0);
        }

        /// <summary>Write to verified writable data; never changes code-page permissions.</summary>
        public bool WriteData(ulong address, byte[] data) =>
            Native.WriteProcessMemory(Handle, (IntPtr)(long)address, data, (IntPtr)data.Length, out var written)
            && written.ToInt64() == data.Length;

        /// <summary>Write the requested range and flush executable changes.</summary>
        public bool WriteBytes(ulong address, byte[] data)
        {
            if (!Native.VirtualProtectEx(Handle, (IntPtr)(long)address, (IntPtr)data.Length,
                                         Native.PAGE_EXECUTE_READWRITE, out var old)) return false;
            bool ok;
            try
            {
                ok = WriteData(address, data);
                if (ok) ok = Native.FlushInstructionCache(Handle, (IntPtr)(long)address, (UIntPtr)data.Length);
            }
            finally
            {
                Native.VirtualProtectEx(Handle, (IntPtr)(long)address, (IntPtr)data.Length, old, out _);
            }
            return ok;
        }

        public bool WriteInt32(ulong address, int value) => WriteData(address, BitConverter.GetBytes(value));

        public ulong Allocate(int size)
        {
            var p = Native.VirtualAllocEx(Handle, IntPtr.Zero, (IntPtr)size,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            return p == IntPtr.Zero ? 0UL : (ulong)p.ToInt64();
        }

        /// <summary>
        /// Runs only an aligned LOCK CMPXCHG on a validated slot. Unity APIs are not invoked
        /// from the helper thread. Both temporary pages are freed only after that thread exits.
        /// </summary>
        private sealed record PendingExchange(ulong Page, IntPtr Thread, ulong Slot, ulong Expected,
            ulong Replacement, bool RollbackOnAbandon);
        private PendingExchange _pendingExchange;
        public bool HasPendingPointerExchange => _pendingExchange != null;

        public void ReconcilePointerExchange()
        {
            var pending = _pendingExchange;
            if (pending != null)
                CompareExchangePointer(pending.Slot, pending.Expected, pending.Replacement,
                    out _, pending.RollbackOnAbandon);
        }

        public bool CompareExchangePointer(ulong slot, ulong expected, ulong replacement, out ulong observed,
            bool rollbackOnAbandon = false)
        {
            if (slot % 8 != 0) throw new ArgumentException("Pointer slot must be aligned", nameof(slot));
            observed = 0;
            if (_pendingExchange != null)
            {
                if (_pendingExchange.Slot != slot || _pendingExchange.Expected != expected || _pendingExchange.Replacement != replacement)
                    throw new InvalidOperationException("A previous pointer exchange must be reconciled before another operation");
            }
            else
            {
                ulong page = Allocate(0x2000);
                if (page == 0) throw new InvalidOperationException("Cannot allocate compare/exchange helper");
                try
                {
                    var args = new byte[32];
                    BitConverter.GetBytes(slot).CopyTo(args, 0);
                    BitConverter.GetBytes(expected).CopyTo(args, 8);
                    BitConverter.GetBytes(replacement).CopyTo(args, 16);
                    BitConverter.GetBytes(ulong.MaxValue).CopyTo(args, 24);
                    byte[] code = { 0x4C,0x8B,0x09, 0x48,0x8B,0x41,0x08, 0x48,0x8B,0x51,0x10,
                        0xF0,0x49,0x0F,0xB1,0x11, 0x48,0x89,0x41,0x18, 0x31,0xC0,0xC3 };
                    if (!WriteData(page, args) || !WriteData(page + 0x1000, code)
                        || !MakeExecutable(page + 0x1000, 0x1000))
                        throw new InvalidOperationException("Cannot prepare compare/exchange helper");
                    var thread = Native.CreateRemoteThread(Handle, IntPtr.Zero, UIntPtr.Zero,
                        (IntPtr)(long)(page + 0x1000), (IntPtr)(long)page, 0, out _);
                    if (thread == IntPtr.Zero)
                        throw new InvalidOperationException("CreateRemoteThread failed (error " + Marshal.GetLastWin32Error() + ")");
                    _pendingExchange = new(page, thread, slot, expected, replacement, rollbackOnAbandon);
                }
                catch { Free(page); throw; }
            }
            var pending = _pendingExchange;
            if (Native.WaitForSingleObject(pending.Thread, 10000) != 0)
                throw new InvalidOperationException("Pointer exchange still pending; ownership is unknown and its pages/handle were retained");
            try
            {
                if (!Native.GetExitCodeThread(pending.Thread, out var code) || code != 0)
                    throw new InvalidOperationException($"Pointer exchange helper failed (exit 0x{code:X}); no success is assumed");
                observed = ReadUInt64(pending.Page + 24);
                if (observed == ulong.MaxValue) throw new InvalidOperationException("Pointer exchange result was not published");
                return observed == expected;
            }
            finally
            {
                _pendingExchange = null;
                Native.CloseHandle(pending.Thread);
                Free(pending.Page);
            }
        }

        // ------------------------------------------------------- remote allocation
        /// <summary>
        /// Allocate memory in the target as close as possible to <paramref name="near"/>,
        /// so a 32-bit relative jump can reach it.
        /// </summary>
        public ulong AllocateNear(ulong near, int size)
        {
            const ulong granularity = 0x10000;
            const ulong maxDistance = 0x70000000;

            ulong anchor = near & ~(granularity - 1);
            for (ulong delta = 0; delta < maxDistance; delta += granularity)
            {
                foreach (var probe in new[] { anchor + delta, anchor - delta })
                {
                    if (probe == 0) continue;
                    var p = Native.VirtualAllocEx(Handle, (IntPtr)(long)probe, (IntPtr)size,
                                                  Native.MEM_COMMIT | Native.MEM_RESERVE,
                                                  Native.PAGE_READWRITE);
                    if (p != IntPtr.Zero) return (ulong)p.ToInt64();
                }
            }

            var fallback = Native.VirtualAllocEx(Handle, IntPtr.Zero, (IntPtr)size,
                                                 Native.MEM_COMMIT | Native.MEM_RESERVE,
                                                 Native.PAGE_READWRITE);
            return fallback == IntPtr.Zero ? 0UL : (ulong)fallback.ToInt64();
        }

        public bool MakeExecutable(ulong address, int size) =>
            Native.VirtualProtectEx(Handle, (IntPtr)(long)address, (IntPtr)size,
                                    Native.PAGE_EXECUTE_READ, out _)
            && Native.FlushInstructionCache(Handle, (IntPtr)(long)address, (UIntPtr)size);

        public bool Free(ulong address) =>
            address != 0 &&
            Native.VirtualFreeEx(Handle, (IntPtr)(long)address, IntPtr.Zero, Native.MEM_RELEASE);

        public void Dispose()
        {
            var handle = Handle;
            if (handle == IntPtr.Zero) return;
            Handle = IntPtr.Zero;
            var pending = _pendingExchange;
            _pendingExchange = null;
            if (pending == null) { Native.CloseHandle(handle); return; }
            // A timed-out helper may still publish later. Keep its handle/arguments alive,
            // then compensate a late installation only if that exact pointer still owns the slot.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (Native.WaitForSingleObject(pending.Thread, uint.MaxValue) == 0
                        && Native.GetExitCodeThread(pending.Thread, out uint result) && result == 0
                        && Native.GetExitCodeProcess(handle, out uint state) != 0 && state == 259
                        && pending.RollbackOnAbandon)
                    {
                        using var recovery = new TargetProcess(Pid);
                        recovery.CompareExchangePointer(pending.Slot, pending.Replacement, pending.Expected, out _);
                    }
                }
                catch { /* A failed late recovery is not reported as a completed restoration. */ }
                finally
                {
                    Native.VirtualFreeEx(handle, (IntPtr)(long)pending.Page, IntPtr.Zero, Native.MEM_RELEASE);
                    Native.CloseHandle(pending.Thread);
                    Native.CloseHandle(handle);
                }
            });
        }

        // -------------------------------------------------------------- discovery
        public static IEnumerable<uint> FindProcessIds(string exeName)
        {
            var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == (IntPtr)(-1)) yield break;
            try
            {
                var pe = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
                if (!Native.Process32FirstW(snap, ref pe)) yield break;
                do
                {
                    if (string.Equals(pe.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                        yield return pe.th32ProcessID;
                } while (Native.Process32NextW(snap, ref pe));
            }
            finally { Native.CloseHandle(snap); }
        }

        public static uint FindProcessId(string exeName)
        {
            foreach (var pid in FindProcessIds(exeName)) return pid;
            return 0;
        }

        private static List<(ulong Base, uint Size, string Module, string Path)> Modules(uint pid)
        {
            var list = new List<(ulong, uint, string, string)>();
            var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
            if (snap == (IntPtr)(-1)) return list;
            try
            {
                var me = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
                if (!Native.Module32FirstW(snap, ref me)) return list;
                do
                {
                    list.Add(((ulong)me.modBaseAddr.ToInt64(), me.modBaseSize, me.szModule, me.szExePath));
                } while (Native.Module32NextW(snap, ref me));
            }
            finally { Native.CloseHandle(snap); }
            return list;
        }

        public static (ulong Base, uint Size, string Path) FindModule(uint pid, string moduleName)
        {
            foreach (var m in Modules(pid))
                if (string.Equals(m.Module, moduleName, StringComparison.OrdinalIgnoreCase))
                    return (m.Base, m.Size, m.Path);
            return (0, 0, null);
        }

        /// <summary>Path of the process' own executable (first module in the snapshot).</summary>
        public static (ulong Base, uint Size, string Path) FindMainModule(uint pid)
        {
            foreach (var m in Modules(pid))
                if (m.Module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (m.Base, m.Size, m.Path);
            return (0, 0, null);
        }
    }
}

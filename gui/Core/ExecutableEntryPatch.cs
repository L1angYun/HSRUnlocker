using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StarRailUnlocker.Core
{
    /// <summary>
    /// Exchanges an aligned entry word only while every enumerated game thread is suspended
    /// and none has its instruction pointer in the displaced instructions. The caller owns
    /// the gateway and must retain all published executable storage until process exit.
    /// </summary>
    public sealed class ExecutableEntryPatch
    {
        // The only supported displacement consists entirely of stack/nonvolatile saves.
        // No relative instruction is copied and the complete SUB RSP instruction is replayed.
        private static readonly byte[] Prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };
        private static readonly object ExchangeLock = new();
        private readonly TargetProcess _process;
        private readonly ulong _entry;
        private readonly byte[] _original;
        private readonly object MutationLock = new();
        private byte[] _replacement;
        private uint _pendingProtection;
        private bool _protectionNeedsRestore;
        public bool IsInstalled { get; private set; }
        public bool WasPublished { get; private set; }
        public string LastError { get; private set; }

        public ExecutableEntryPatch(TargetProcess process, ulong entry, byte[] originalInstructions)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            if (entry == 0 || entry % 8 != 0) throw new ArgumentException(L10n.T("Resolver.EntryPatch.EntryMustBeAligned"), nameof(entry));
            if (originalInstructions == null || !originalInstructions.AsSpan().SequenceEqual(Prologue))
                throw new NotSupportedException(L10n.T("Resolver.EntryPatch.UnsupportedEntryDisplacement"));
            _entry = entry;
            _original = (byte[])originalInstructions.Clone();
        }

        public static byte[] BuildEntryWord(ulong entry, ulong wrapper)
        {
            long displacement = checked((long)wrapper - checked((long)entry + 5));
            var word = new byte[] { 0xE9, 0, 0, 0, 0, 0x0F, 0x1F, 0x00 };
            BitConverter.GetBytes(checked((int)displacement)).CopyTo(word, 1);
            return word;
        }

        public bool Install(ulong wrapper, Action<string> log = null)
        {
            lock (MutationLock)
            {
                LastError = null;
                try
                {
                    if (_protectionNeedsRestore && !RestorePendingProtection())
                        throw new InvalidOperationException(LastError ?? L10n.T("Resolver.EntryPatch.ProtectionRestorationPending"));
                    var next = BuildEntryWord(_entry, wrapper);
                    if (_replacement != null && !_replacement.AsSpan().SequenceEqual(next))
                        throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.PublishedEntryCannotBeRedirected"));
                    if (IsInstalled)
                    {
                        if (_process.Read(_entry, 8) is not { } live || !live.AsSpan().SequenceEqual(next))
                            throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.PublishedEntryOwnershipChanged"));
                        return true;
                    }
                    _replacement = next;
                    if (!Exchange(_original.AsSpan(0, 8).ToArray(), _replacement, true)) return false;
                    IsInstalled = true;
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; log?.Invoke(L10n.T("Resolver.EntryPatch.InstallFailed", LastError)); return false; }
            }
        }

        public bool Remove(Action<string> log = null)
        {
            lock (MutationLock)
            {
                if (!IsInstalled && !WasPublished && !_protectionNeedsRestore) return true;
                LastError = null;
                try
                {
                    if (!_process.IsAlive) { IsInstalled = false; _protectionNeedsRestore = false; return true; }
                    if (!IsInstalled)
                    {
                        // The write may have reached the target even when its acknowledgement failed.
                        byte[] current = _process.Read(_entry, _original.Length);
                        if (current == null)
                            throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.CannotReconcilePublishedEntry"));
                        if (_replacement != null && current.AsSpan(0, 8).SequenceEqual(_replacement)
                            && current.AsSpan(8).SequenceEqual(_original.AsSpan(8)))
                            IsInstalled = true;
                        else if (!current.AsSpan().SequenceEqual(_original))
                            throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.OwnershipChangedRestorationPending"));
                    }
                    if (!IsInstalled)
                    {
                        if (!Native.FlushInstructionCache(_process.Handle, (IntPtr)(long)_entry, (UIntPtr)8))
                            throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.CannotFlushRestoredEntry"));
                        if (!RestorePendingProtection())
                            throw new InvalidOperationException(LastError ?? L10n.T("Resolver.EntryPatch.ProtectionRestorationPending"));
                        return true;
                    }
                    if (!Exchange(_replacement, _original.AsSpan(0, 8).ToArray(), false)) return false;
                    IsInstalled = false;
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; log?.Invoke(L10n.T("Resolver.EntryPatch.RestoreFailed", LastError)); return false; }
            }
        }

        private bool Exchange(byte[] expected, byte[] replacement, bool installing)
        {
            lock (ExchangeLock)
                return ExchangeLocked(expected, replacement, installing);
        }

        private bool ExchangeLocked(byte[] expected, byte[] replacement, bool installing)
        {
            if (_process.Pid == (uint)Environment.ProcessId)
                throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.RefusingSelfSuspension"));
            using var pause = new PausedThreads(_process.Pid, _entry, _original.Length);
            byte[] current = _process.Read(_entry, _original.Length);
            if (current == null) throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.CannotReadEntry"));
            if (!current.AsSpan(0, 8).SequenceEqual(expected))
                throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.OwnershipChangedNoOverwrite"));
            if (installing && !current.AsSpan(8).SequenceEqual(_original.AsSpan(8)))
                throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.SuffixChangedInstallRefused"));

            if (!Native.VirtualProtectEx(_process.Handle, (IntPtr)(long)_entry, (IntPtr)8,
                Native.PAGE_EXECUTE_READWRITE, out uint oldProtection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Resolver.EntryPatch.CannotEnableEntryWrite"));
            bool wrote = false, restoredProtection = false;
            uint restoreProtection = _protectionNeedsRestore ? _pendingProtection : oldProtection;
            try
            {
                // Set before the first write: even a failed/partial publication retains its gateway.
                if (installing) WasPublished = true;
                wrote = _process.WriteData(_entry, replacement)
                    && _process.Read(_entry, 8) is { } readback && readback.AsSpan().SequenceEqual(replacement)
                    && Native.FlushInstructionCache(_process.Handle, (IntPtr)(long)_entry, (UIntPtr)8);
                if (!wrote)
                {
                    bool rolledBack = _process.WriteData(_entry, expected)
                        && _process.Read(_entry, 8) is { } prior && prior.AsSpan().SequenceEqual(expected)
                        && Native.FlushInstructionCache(_process.Handle, (IntPtr)(long)_entry, (UIntPtr)8);
                    if (!rolledBack) throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.WriteAndRollbackFailed"));
                }
            }
            finally
            {
                restoredProtection = Native.VirtualProtectEx(_process.Handle, (IntPtr)(long)_entry, (IntPtr)8,
                    restoreProtection, out _);
                if (restoredProtection) _protectionNeedsRestore = false;
                else { _pendingProtection = restoreProtection; _protectionNeedsRestore = true; }
                // A failed cache flush or rollback must not hide an exact published detour.
                byte[] live = _process.Read(_entry, _original.Length);
                if (live != null)
                {
                    if (_replacement != null && live.AsSpan(0, 8).SequenceEqual(_replacement))
                        IsInstalled = true;
                    else if (live.AsSpan().SequenceEqual(_original))
                        IsInstalled = false;
                }
            }
            if (!restoredProtection) throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.CannotRestoreProtection"));
            if (!wrote) throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.WriteFailedRolledBack"));
            return true;
        }

        private bool RestorePendingProtection()
        {
            if (!_protectionNeedsRestore) return true;
            if (!_process.IsAlive) { _protectionNeedsRestore = false; return true; }
            lock (ExchangeLock)
            {
                using var pause = new PausedThreads(_process.Pid, _entry, _original.Length);
                if (!Native.VirtualProtectEx(_process.Handle, (IntPtr)(long)_entry, (IntPtr)8,
                    Native.PAGE_EXECUTE_READWRITE, out _))
                {
                    LastError = L10n.T("Resolver.EntryPatch.CannotReopenEntryForProtectionRestore");
                    return false;
                }
                bool restored = Native.VirtualProtectEx(_process.Handle, (IntPtr)(long)_entry, (IntPtr)8,
                    _pendingProtection, out _);
                if (restored) _protectionNeedsRestore = false;
                else LastError = L10n.T("Resolver.EntryPatch.ProtectionRestorationStillPending");
                return restored;
            }
        }

        private sealed class PausedThreads : IDisposable
        {
            private readonly Dictionary<uint, IntPtr> _handles = new();
            private IntPtr _contextStorage;
            public PausedThreads(uint pid, ulong entry, int length)
            {
                try
                {
                    _contextStorage = Marshal.AllocHGlobal(1232 + 15);
                    IntPtr context = (IntPtr)((_contextStorage.ToInt64() + 15) & ~15L);
                    bool stable = false;
                    // Re-enumerate after suspension to include threads created during acquisition.
                    for (int round = 0; round < 4; round++)
                    {
                        var threads = Enumerate(pid);
                        foreach (uint id in threads)
                        {
                            if (_handles.ContainsKey(id)) continue;
                            IntPtr handle = OpenThread(0x004A, false, id); // query, context, suspend/resume
                            if (handle == IntPtr.Zero)
                                throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Resolver.EntryPatch.CannotInspectEveryThread"));
                            if (SuspendThread(handle) == uint.MaxValue)
                            {
                                Native.CloseHandle(handle);
                                throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Resolver.EntryPatch.CannotPauseEveryThread"));
                            }
                            _handles.Add(id, handle);
                        }
                        var after = Enumerate(pid);
                        stable = after.Count > 0;
                        foreach (uint id in after) if (!_handles.ContainsKey(id)) { stable = false; break; }
                        if (stable) break;
                    }
                    if (!stable) throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.ThreadListUnstable"));
                    foreach (IntPtr handle in _handles.Values)
                    {
                        Marshal.WriteInt32(context, 48, 0x00100001); // AMD64 CONTEXT_CONTROL
                        if (!GetThreadContext(handle, context))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Resolver.EntryPatch.CannotVerifyPausedInstructionPointer"));
                        ulong ip = unchecked((ulong)Marshal.ReadInt64(context, 248));
                        if (ip >= entry && ip - entry < (ulong)length)
                            throw new InvalidOperationException(L10n.T("Resolver.EntryPatch.ThreadEnteringFadeFunction"));
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                foreach (IntPtr handle in _handles.Values)
                {
                    // Balance exactly the one suspend increment owned by this instance.
                    ResumeThread(handle);
                    Native.CloseHandle(handle);
                }
                _handles.Clear();
                if (_contextStorage != IntPtr.Zero) { Marshal.FreeHGlobal(_contextStorage); _contextStorage = IntPtr.Zero; }
            }
            private static HashSet<uint> Enumerate(uint pid)
            {
                IntPtr snapshot = Native.CreateToolhelp32Snapshot(4, 0);
                if (snapshot == (IntPtr)(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Resolver.EntryPatch.CannotEnumerateThreads"));
                try
                {
                    var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadEntry>() };
                    var result = new HashSet<uint>();
                    if (!Thread32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    do
                    {
                        if (entry.Owner == pid) result.Add(entry.Id);
                        entry.Size = (uint)Marshal.SizeOf<ThreadEntry>();
                    } while (Thread32Next(snapshot, ref entry));
                    int error = Marshal.GetLastWin32Error();
                    if (error != 18) throw new Win32Exception(error, L10n.T("Resolver.EntryPatch.ThreadEnumerationIncomplete"));
                    return result;
                }
                finally { Native.CloseHandle(snapshot); }
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry
        {
            public uint Size, Usage, Id, Owner;
            public int Priority, Delta;
            public uint Flags;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    }
}

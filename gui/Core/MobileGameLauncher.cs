using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StarRailUnlocker.Core
{
    /// <summary>
    /// Starts a fresh client through its named Unity entry point, before its primary thread runs.
    /// Only our bootstrap executes in the target; no external DLL or game code patch is used.
    /// </summary>
    public static class MobileGameLauncher
    {
        public static uint Launch(string executable, System.Collections.Generic.IReadOnlyList<string> arguments, Action<string> log = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executable = Path.GetFullPath(executable);
            string enginePath = Path.Combine(Path.GetDirectoryName(executable), "UnityPlayer.dll");
            // Resolve named interfaces from the selected files before creating anything.
            var appImage = new PeImage(executable);
            var engineImage = new PeImage(enginePath);
            var platform = GameBuildResolver.ResolveNamedRegistration(engineImage, "UnityEngine.Application::get_platform");
            uint slotRva = MobileEntryResolver.FindUnityMainImport(executable);
            uint exportRva = MobileEntryResolver.FindUnityMainExport(enginePath);
            string command = BuildCommandLine(arguments);
            byte[] commandBytes = Encoding.Unicode.GetBytes(command + "\0");
            if (commandBytes.Length > 60000) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ArgumentsTooLong"));
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            if (executable.Contains('"')) throw new ArgumentException(L10n.T("Runtime.MobileLauncher.PathQuote"), nameof(executable));
            var processCommand = new StringBuilder("\"" + executable + "\" " + command);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CreateProcessW(executable, processCommand, IntPtr.Zero, IntPtr.Zero, false,
                4, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.SuspendedStartFailed"));
            ulong state = 0, code = 0; IntPtr worker = IntPtr.Zero;
            RemoteFunctionTable unwind = null;
            bool committed = false;
            try
            {
                using var target = new TargetProcess(info.ProcessId);
                ulong imageBase = ReadImageBase(info.Process);
                ValidateImage(target, imageBase, appImage);
                state = target.Allocate(checked(64 + commandBytes.Length));
                code = target.Allocate(4096);
                if (state == 0 || code == 0) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.StateAllocateFailed"));
                var data = new byte[64 + commandBytes.Length];
                BitConverter.GetBytes(imageBase).CopyTo(data, 8);
                BitConverter.GetBytes(state + 64).CopyTo(data, 16);
                commandBytes.CopyTo(data, 64);
                byte[] bootstrap = BuildBootstrap();
                if (!target.WriteData(state, data) || !target.WriteData(code, bootstrap))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.WriteFailed"));
                if (!Native.VirtualProtectEx(target.Handle, (IntPtr)(long)code, (IntPtr)4096, Native.PAGE_EXECUTE_READ, out _)
                    || !Native.FlushInstructionCache(target.Handle, (IntPtr)(long)code, (UIntPtr)bootstrap.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.CodePageFailed"));
                unwind = new RemoteFunctionTable(target, code, bootstrap, BuildBootstrapUnwindInfo());
                unwind.Register();
                worker = Native.CreateRemoteThread(target.Handle, IntPtr.Zero, UIntPtr.Zero,
                    (IntPtr)(long)code, (IntPtr)(long)state, 0, out _);
                if (worker == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.ThreadStartFailed"));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (target.ReadInt32(state + 4) != 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!target.IsAlive || Native.WaitForSingleObject(worker, 0) == 0)
                        throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ThreadExited"));
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException(L10n.T("Runtime.MobileLauncher.LoaderTimeout"));
                    Thread.Sleep(20);
                }
                var engine = TargetProcess.FindModule(info.ProcessId, "UnityPlayer.dll");
                if (engine.Base == 0 || !string.Equals(Path.GetFullPath(engine.Path), enginePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.EngineMismatch"));
                ValidateImage(target, engine.Base, engineImage);
                InstallMobilePlatform(target, engine.Base, engineImage, platform, log);
                ulong entry = checked(engine.Base + exportRva);
                var entryBytes = target.Read(entry, 32);
                if (entryBytes == null || !entryBytes.AsSpan().SequenceEqual(engineImage.ReadRva(exportRva, 32)))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.EntryChanged"));
                ulong exit = ResolveExitProcess(target);
                if (!target.WriteData(state + 40, BitConverter.GetBytes(exit)))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ExitEntryWriteFailed"));
                if (target.ReadUInt64(imageBase + slotRva) != entry)
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ImportMismatch"));
                if (!target.WriteData(state + 24, BitConverter.GetBytes(entry))) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.EntryWriteFailed"));
                cancellationToken.ThrowIfCancellationRequested();
                log?.Invoke(L10n.T("Runtime.MobileLauncher.EntryVerified"));
                if (!target.WriteInt32(state, 1)) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.CommitFailed"));
                // Once the target has entered UnityMain, its arguments and code stay valid until process exit.
                while (target.ReadInt32(state + 4) == 1)
                {
                    if (!target.IsAlive) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ClientExited"));
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException(L10n.T("Runtime.MobileLauncher.CommitTimeout"));
                    Thread.Sleep(10);
                }
                if (!target.IsAlive || target.ReadInt32(state + 4) != 2)
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.EntryNotRunning"));
                committed = true;
                log?.Invoke(L10n.T("Runtime.MobileLauncher.EntryRunning"));
                return info.ProcessId;
            }
            finally
            {
                if (!committed)
                {
                    // This is exclusively the suspended process created by this method, never an attached client.
                    // Do not resume an incomplete or failed bootstrap into an unknown state.
                    bool exited = Native.WaitForSingleObject(info.Process, 0) == 0;
                    if (!exited)
                    {
                        bool terminated = TerminateProcess(info.Process, 0xE002);
                        int error = terminated ? 0 : Marshal.GetLastWin32Error();
                        exited = Native.WaitForSingleObject(info.Process, 5000) == 0;
                        if (!exited)
                            log?.Invoke(L10n.T("Runtime.MobileLauncher.CleanupUnconfirmed", info.ProcessId, error));
                    }
                }
                if (worker != IntPtr.Zero) Native.CloseHandle(worker);
                // Registered or pending tables can still reference bootstrap code after worker creation fails.
                // Bootstrap state/code and registration storage remain owned by this process until its exit.
                unwind?.Dispose();
                Native.CloseHandle(info.Thread); Native.CloseHandle(info.Process);
            }
        }

        internal static string BuildCommandLine(System.Collections.Generic.IReadOnlyList<string> arguments)
        {
            if (arguments == null || arguments.Count != 4 || arguments[0] != "-platform_type"
                || arguments[1] != "CLOUD_WEB_TOUCH" || arguments[2] != "-graphics_setting")
                throw new ArgumentException(L10n.T("Runtime.MobileLauncher.UnverifiedArguments"), nameof(arguments));
            byte[] decoded;
            try { decoded = Convert.FromBase64String(arguments[3]); }
            catch (FormatException ex) { throw new ArgumentException(L10n.T("Runtime.MobileLauncher.InvalidGraphics"), nameof(arguments), ex); }
            if (decoded.Length == 0 || Convert.ToBase64String(decoded) != arguments[3])
                throw new ArgumentException(L10n.T("Runtime.MobileLauncher.NonCanonicalGraphics"), nameof(arguments));
            return string.Join(" ", arguments);
        }

        internal static byte[] BuildBootstrap() => new byte[]
        {
            0x53,                               // push rbx; preserve nonvolatile, align stack
            0x48,0x83,0xEC,0x20,                 // shadow space
            0x48,0x89,0xCB,                      // rbx = private state
            0xC7,0x43,0x04,0x01,0,0,0,           // status = ready (Windows loader has completed)
            0x8B,0x03,                          // loop: eax = gate
            0x83,0xF8,0x01,                      // gate == proceed?
            0x74,0x0B,                          // je call
            0x83,0xF8,0x02,                      // gate == cancel?
            0x74,0x3B,                          // je return
            0xF3,0x90,                          // pause
            0xEB,0xF0,                          // jmp loop
            0x90,0x90,                          // padding
            0xC7,0x43,0x04,0x02,0,0,0,           // call: status = entering
            0x48,0x8B,0x4B,0x08,                 // rcx = HINSTANCE
            0x31,0xD2,                          // rdx = previous instance (null)
            0x4C,0x8B,0x43,0x10,                 // r8 = UTF-16 command line
            0x41,0xB9,0x0A,0,0,0,                // r9d = SW_SHOWDEFAULT
            0x48,0x8B,0x43,0x18,                 // rax = validated UnityMain
            0xFF,0xD0,                          // call rax
            0x89,0x43,0x20,                      // result = eax
            0xC7,0x43,0x04,0x03,0,0,0,           // status = returned
            0x48,0x8B,0x43,0x28,                 // rax = named ExitProcess (zero only in own fixture)
            0x48,0x85,0xC0,
            0x74,0x05,                          // fixture returns without terminating the test process
            0x8B,0x4B,0x20,                      // ecx = entry result
            0xFF,0xD0,                          // ExitProcess(result)
            0x48,0x83,0xC4,0x20,                 // return: release shadow space
            0x5B,0xC3                           // pop rbx; ret
        };
        internal static byte[] BuildBootstrapUnwindInfo() =>
            new byte[] { 0x01, 0x05, 0x02, 0x00, 0x05, 0x32, 0x01, 0x30 }; // ALLOC_SMALL 0x20; PUSH RBX.
        internal static byte[] BuildPlatformGetter() => new byte[] { 0xB8, 11, 0, 0, 0, 0xC3 }; // RuntimePlatform.Android

        private static void InstallMobilePlatform(TargetProcess target, ulong engineBase, PeImage image,
            GameBuildResolver.NamedRegistration platform, Action<string> log)
        {
            void Verify(ulong rva, byte[] expected)
            {
                var live = target.Read(checked(engineBase + rva), expected.Length);
                if (live == null || !live.AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PlatformLayoutMismatch"));
            }
            Verify(platform.EntryRva, platform.Entry);
            Verify(platform.LoopRva, platform.Loop);
            Verify(platform.KeysRva, platform.Keys);
            Verify(platform.NativeTargetRva, image.ReadRva(platform.NativeTargetRva, 32));
            ulong slot = checked(engineBase + platform.TableEntryRva);
            ulong original = checked(engineBase + platform.NativeTargetRva);
            if (slot % 8 != 0 || target.ReadUInt64(slot) != original)
                throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PlatformPointerChanged"));
            bool dataSection = false;
            foreach (var section in image.Sections)
                if ((section.Flags & 0x40000000) != 0 && (section.Flags & 0x20000000) == 0
                    && platform.TableEntryRva >= section.Rva
                    && platform.TableEntryRva + 8 <= (ulong)section.Rva + section.VirtualSize)
                    dataSection = true;
            if (!dataSection) throw new InvalidDataException(L10n.T("Runtime.MobileLauncher.PlatformSectionInvalid"));
            ulong getter = target.Allocate(4096);
            if (getter == 0 || !target.WriteData(getter, BuildPlatformGetter()) || !target.MakeExecutable(getter, 4096))
                throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PlatformFunctionFailed"));
            if (!Native.VirtualProtectEx(target.Handle, (IntPtr)(long)slot, (IntPtr)8, Native.PAGE_READWRITE, out uint previous))
                throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.PlatformPageFailed"));
            try
            {
                if (!target.CompareExchangePointer(slot, original, getter, out _))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PlatformRegistrationChanged"));
            }
            finally
            {
                if (!Native.VirtualProtectEx(target.Handle, (IntPtr)(long)slot, (IntPtr)8, previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.PlatformProtectionFailed"));
            }
            if (target.ReadUInt64(slot) != getter) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PlatformReplacementUnconfirmed"));
            // The registration and all later IL2CPP caches can retain this function until game exit.
            log?.Invoke(L10n.T("Runtime.MobileLauncher.PlatformReplaced", platform.TableEntryRva));
        }

        private static ulong ResolveExitProcess(TargetProcess target)
        {
            IntPtr localEntry = GetProcAddress(GetModuleHandleW("kernel32.dll"), "ExitProcess");
            if (localEntry == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Runtime.MobileLauncher.ExitResolveFailed"));
            ulong address = (ulong)localEntry.ToInt64();
            using var current = Process.GetCurrentProcess();
            foreach (ProcessModule local in current.Modules)
            {
                ulong start = (ulong)local.BaseAddress.ToInt64();
                if (address < start || address - start >= (ulong)local.ModuleMemorySize) continue;
                ulong rva = address - start;
                var disk = new PeImage(local.FileName);
                var remote = TargetProcess.FindModule(target.Pid, local.ModuleName);
                if (remote.Base == 0 || !string.Equals(Path.GetFullPath(remote.Path), Path.GetFullPath(local.FileName), StringComparison.OrdinalIgnoreCase)
                    || !disk.IsExecutableRva(rva, 16))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ExitEntryMismatch"));
                ValidateImage(target, remote.Base, disk);
                var bytes = target.Read(remote.Base + rva, 16);
                if (bytes == null || !bytes.AsSpan().SequenceEqual(disk.ReadRva(rva, 16)))
                    throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ExitEntryChanged"));
                return remote.Base + rva;
            }
            throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ExitModuleUnknown"));
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true, SetLastError=true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static ulong ReadImageBase(IntPtr process)
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status < 0 || info.Peb == IntPtr.Zero) throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.PebReadFailed"));
            var bytes = new byte[8];
            if (!Native.ReadProcessMemory(process, info.Peb + 0x10, bytes, (IntPtr)8, out var read) || read.ToInt64() != 8)
                throw new InvalidOperationException(L10n.T("Runtime.MobileLauncher.ImageBaseReadFailed"));
            return BitConverter.ToUInt64(bytes);
        }
        private static void ValidateImage(TargetProcess target, ulong address, PeImage disk)
        {
            var head = target.Read(address, 4096);
            if (head == null || BitConverter.ToUInt16(head, 0) != 0x5A4D) throw new InvalidDataException(L10n.T("Runtime.MobileLauncher.ImageInvalid"));
            int pe = BitConverter.ToInt32(head, 0x3c);
            if (pe < 0x40 || pe > head.Length - 264 || BitConverter.ToUInt32(head, pe) != 0x4550
                || BitConverter.ToUInt16(head, pe + 4) != 0x8664
                || BitConverter.ToUInt32(head, pe + 8) != disk.Timestamp
                || BitConverter.ToUInt32(head, pe + 24 + 56) != disk.SizeOfImage)
                throw new InvalidDataException(L10n.T("Runtime.MobileLauncher.ImageIdentityMismatch"));
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
        { internal IntPtr Reserved1, Peb, Reserved2A, Reserved2B, ProcessId, Reserved3; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
        {
            internal uint Size; internal IntPtr Reserved, Desktop, Title;
            internal uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
            internal ushort Show,ReservedSize;
            internal IntPtr Reserved2,Input,Output,Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int kind, ref ProcessBasicInformation info, int size, out int returned);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    }
}

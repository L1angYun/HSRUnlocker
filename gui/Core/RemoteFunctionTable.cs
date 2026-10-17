using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace StarRailUnlocker.Core
{
    /// <summary>
    /// Registers verified generated code in the target's unwind tables.
    /// Registration and pending helpers retain both their storage and the caller-owned code.
    /// </summary>
    public sealed class RemoteFunctionTable : IDisposable
    {
        private const int StorageSize = 0x2000, HelperOffset = 0x1000, UnwindOffset = 0x1100, TableOffset = 0x1120, GatewaySize = 19;
        private static readonly byte[] Prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };
        private readonly TargetProcess _process;
        private readonly ulong _gateway;
        private readonly byte[] _expectedCode, _unwindInfo;
        private readonly object _gate = new();
        private ulong _storage;
        private IntPtr _thread;
        private bool _disposed, _registrationUncertain;

        public bool IsRegistered { get; private set; }
        public bool MustRetainGateway { get; private set; }
        public bool MustRetainCode => MustRetainGateway;

        public RemoteFunctionTable(TargetProcess process, ulong gatewayAddress)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            if (gatewayAddress == 0) throw new ArgumentOutOfRangeException(nameof(gatewayAddress));
            _gateway = gatewayAddress;
        }

        public RemoteFunctionTable(TargetProcess process, ulong codeAddress, byte[] expectedCode, byte[] unwindInfo)
            : this(process, codeAddress)
        {
            if (expectedCode == null || expectedCode.Length == 0 || expectedCode.Length > 0x1000)
                throw new ArgumentException("A bounded complete generated function is required", nameof(expectedCode));
            // This owner accepts independent, handler-free version-1 unwind records only.
            if (unwindInfo == null || unwindInfo.Length < 4 || unwindInfo[0] != 1
                || unwindInfo[1] > expectedCode.Length
                || unwindInfo.Length != ((4 + unwindInfo[2] * 2 + 3) & ~3)
                || unwindInfo.Length > TableOffset - UnwindOffset)
                throw new ArgumentException("A bounded independent unwind record is required", nameof(unwindInfo));
            _expectedCode = (byte[])expectedCode.Clone();
            _unwindInfo = (byte[])unwindInfo.Clone();
        }

        public void Register()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RemoteFunctionTable));
                if (!_process.IsAlive) throw new InvalidOperationException("Target exited before generated-code unwind registration");
                if (IsRegistered) return;
                if (_registrationUncertain)
                    throw new InvalidOperationException("Generated-code unwind registration could not be confirmed; storage is retained and publication is refused");
                if (_thread == IntPtr.Zero)
                {
                    Prepare();
                    _thread = Native.CreateRemoteThread(_process.Handle, IntPtr.Zero, UIntPtr.Zero,
                        (IntPtr)(long)(_storage + HelperOffset), (IntPtr)(long)_storage, 0, out _);
                    if (_thread == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        FreeUnregisteredStorage();
                        throw new Win32Exception(error, "Cannot start generated-code unwind registration");
                    }
                    // Even a timed-out thread may subsequently install a table covering the gateway.
                    MustRetainGateway = true;
                }
                if (Native.WaitForSingleObject(_thread, 10000) != 0)
                    throw new InvalidOperationException("Generated-code unwind registration is pending; helper and code must be retained");
                if (!Native.GetExitCodeThread(_thread, out uint result))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot confirm generated-code unwind registration");
                Native.CloseHandle(_thread);
                _thread = IntPtr.Zero;
                if (result == 0)
                {
                    MustRetainGateway = false;
                    FreeUnregisteredStorage();
                    throw new InvalidOperationException("Target rejected the generated-code unwind table");
                }
                // An exception exit must not be mistaken for BOOLEAN success. Unknown upper EAX bits fail closed.
                if (result != 1)
                {
                    _registrationUncertain = true;
                    throw new InvalidOperationException($"Generated-code unwind helper returned an uncertain result 0x{result:X}; storage is retained");
                }
                IsRegistered = true;
            }
        }

        private void Prepare()
        {
            int codeSize = _expectedCode?.Length ?? GatewaySize;
            byte[] liveCode = _process.Read(_gateway, codeSize);
            byte[] unwind;
            if (_expectedCode != null)
            {
                if (liveCode == null || !liveCode.AsSpan().SequenceEqual(_expectedCode))
                    throw new InvalidOperationException("Generated code differs from its complete verified unwind template");
                unwind = _unwindInfo;
            }
            else
            {
                byte[] branch = { 0x48, 0x39, 0xE4, 0x0F, 0x84 }; // CMP RSP,RSP; JE rel32.
                if (liveCode == null || !liveCode.AsSpan(0, 10).SequenceEqual(Prologue)
                    || !liveCode.AsSpan(10, branch.Length).SequenceEqual(branch))
                    throw new NotSupportedException("Only the complete verified fade gateway supports unwind registration");
                // Descending instruction-end offsets: ALLOC_SMALL 0x50; PUSH RBX/RBP/RDI/RSI/R14.
                unwind = new byte[] { 0x01,0x0A,0x06,0x00, 0x0A,0x92, 0x06,0x30, 0x05,0x50,
                    0x04,0x70, 0x03,0x60, 0x02,0xE0 };
            }
            ulong addFunctionTable = ResolveAddFunctionTable();
            _storage = _process.AllocateNear(_gateway, StorageSize);
            if (_storage == 0) throw new InvalidOperationException("Cannot allocate generated-code unwind metadata");
            try
            {
                ulong imageBase = Math.Min(_gateway, _storage);
                var table = new byte[12];
                BitConverter.GetBytes(checked((uint)(_gateway - imageBase))).CopyTo(table, 0);
                BitConverter.GetBytes(checked((uint)(_gateway + (ulong)codeSize - imageBase))).CopyTo(table, 4);
                BitConverter.GetBytes(checked((uint)(_storage + UnwindOffset - imageBase))).CopyTo(table, 8);
                var arguments = new byte[32];
                BitConverter.GetBytes(addFunctionTable).CopyTo(arguments, 0);
                BitConverter.GetBytes(_storage + TableOffset).CopyTo(arguments, 8);
                BitConverter.GetBytes(1u).CopyTo(arguments, 16);
                BitConverter.GetBytes(imageBase).CopyTo(arguments, 24);
                // A leaf thread entry tail-forwards to RtlAddFunctionTable, preserving its system return address.
                byte[] helper = { 0x48,0x8B,0x01, 0x4C,0x8B,0x41,0x18, 0x8B,0x51,0x10,
                    0x48,0x8B,0x49,0x08, 0xFF,0xE0 };
                if (!_process.WriteData(_storage, arguments)
                    || !_process.WriteData(_storage + HelperOffset, helper)
                    || !_process.WriteData(_storage + UnwindOffset, unwind)
                    || !_process.WriteData(_storage + TableOffset, table))
                    throw new InvalidOperationException("Cannot initialize generated-code unwind registration");
                CameraFadeResolver.Equal(_process, _storage, arguments);
                CameraFadeResolver.Equal(_process, _storage + HelperOffset, helper);
                CameraFadeResolver.Equal(_process, _storage + UnwindOffset, unwind);
                CameraFadeResolver.Equal(_process, _storage + TableOffset, table);
                if (!_process.MakeExecutable(_storage + HelperOffset, 0x1000))
                    throw new InvalidOperationException("Cannot protect generated-code unwind metadata and helper");
            }
            catch { FreeUnregisteredStorage(); throw; }
        }

        private ulong ResolveAddFunctionTable()
        {
            IntPtr entry = GetProcAddress(GetModuleHandleW("ntdll.dll"), "RtlAddFunctionTable");
            if (entry == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve RtlAddFunctionTable");
            ulong address = unchecked((ulong)entry.ToInt64());
            using var current = Process.GetCurrentProcess();
            foreach (ProcessModule local in current.Modules)
            {
                ulong start = unchecked((ulong)local.BaseAddress.ToInt64());
                if (address < start || address - start >= (ulong)local.ModuleMemorySize) continue;
                ulong rva = address - start;
                var disk = new PeImage(local.FileName);
                var remote = TargetProcess.FindModule(_process.Pid, local.ModuleName);
                if (remote.Base == 0 || remote.Size != disk.SizeOfImage
                    || !string.Equals(Path.GetFullPath(remote.Path), Path.GetFullPath(local.FileName), StringComparison.OrdinalIgnoreCase)
                    || !disk.IsExecutableRva(rva, 16))
                    throw new InvalidOperationException("Target unwind registration module does not match the named system export");
                byte[] head = _process.Read(remote.Base, 4096);
                if (head == null || BitConverter.ToUInt16(head, 0) != 0x5A4D)
                    throw new InvalidOperationException("Cannot verify target unwind registration module");
                int pe = BitConverter.ToInt32(head, 0x3C);
                if (pe < 0x40 || pe > head.Length - 264 || BitConverter.ToUInt32(head, pe) != 0x4550
                    || BitConverter.ToUInt16(head, pe + 4) != 0x8664
                    || BitConverter.ToUInt32(head, pe + 8) != disk.Timestamp
                    || BitConverter.ToUInt32(head, pe + 24 + 56) != disk.SizeOfImage)
                    throw new InvalidOperationException("Target unwind registration module identity changed");
                CameraFadeResolver.Equal(_process, checked(remote.Base + rva), disk.ReadRva(rva, 16));
                return checked(remote.Base + rva);
            }
            throw new InvalidOperationException("Cannot establish the owner of the named unwind registration export");
        }

        private void FreeUnregisteredStorage()
        {
            if (_storage == 0 || MustRetainGateway) return;
            _process.Free(_storage);
            _storage = 0;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                // Closing a helper handle does not stop its execution or cancel a late registration.
                if (_thread != IntPtr.Zero) { Native.CloseHandle(_thread); _thread = IntPtr.Zero; }
                FreeUnregisteredStorage();
                _disposed = true;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}

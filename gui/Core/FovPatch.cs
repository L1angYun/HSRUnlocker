using System;
using System.Collections.Generic;

namespace StarRailUnlocker.Core
{
    public sealed record FovStatistics(ulong Calls, ulong Camera, float Input, float Output, float Delta, float? StoredFov = null);

    /// <summary>
    /// Replaces the verified Camera.set_fieldOfView IL2CPP pointer slot. The wrapper changes
    /// XMM1 and tail-jumps to the complete original accessor, preserving its exception paths.
    /// No UnityPlayer instructions are overwritten and no native-camera field offset is assumed.
    /// </summary>
    public sealed class FovPatcher : IDisposable
    {
        internal const int DeltaOffset = 0, OriginalOffset = 8, CounterOffset = 16;
        internal const int CameraOffset = 24, InputOffset = 32, OutputOffset = 36, EnabledOffset = 48;
        private readonly TargetProcess _proc;
        private readonly ResolvedGameBuild _build;
        private ulong _region, _original, _baselineCount;
        private bool _published, _restorationPending;
        public bool IsInstalled { get; private set; }
        public float Delta { get; private set; }
        public ulong SlotAddress => _build.CameraSetterSlot;
        public ulong WrapperAddress => _region == 0 ? 0 : _region + 0x1000;
        public string LastError { get; private set; }

        public FovPatcher(TargetProcess proc, ResolvedGameBuild build)
        {
            _proc = proc;
            _build = build;
        }

        public bool Install(float delta, Action<string> log = null)
        {
            if (!float.IsFinite(delta) || Math.Abs(delta) > 60)
            { LastError = L10n.T("Runtime.Fov.InvalidDelta"); return false; }
            if (delta == 0) return Remove(log);
            if (IsInstalled) return WriteDelta(delta);
            LastError = null;
            try
            {
                if (_region == 0)
                {
                    _original = _proc.ReadUInt64(SlotAddress);
                    if (_original == 0) { LastError = L10n.T("Runtime.Fov.SetterNotInitialized"); return false; }
                    if (_original == _build.CameraGetterTarget)
                        throw new InvalidOperationException(L10n.T("Runtime.Fov.AccessorSame"));
                    _build.VerifyCameraAccessors(_proc);
                    _region = _proc.Allocate(0x2000);
                    if (_region == 0) throw new InvalidOperationException(L10n.T("Runtime.Fov.AllocateFailed"));
                    var state = new byte[0x1000];
                    BitConverter.GetBytes(_original).CopyTo(state, OriginalOffset);
                    BitConverter.GetBytes(delta).CopyTo(state, DeltaOffset);
                    var wrapper = BuildWrapper(WrapperAddress, _region);
                    if (!_proc.WriteData(_region, state) || !_proc.WriteData(WrapperAddress, wrapper)
                        || !_proc.MakeExecutable(WrapperAddress, 0x1000))
                        throw new InvalidOperationException(L10n.T("Runtime.Fov.InitializeFailed"));
                }
                if (!WriteDelta(delta)) return false;
                _baselineCount = _proc.ReadUInt64(_region + CounterOffset);
                // If the helper is delayed, the published wrapper still bypasses ADDSS until
                // the caller has confirmed ownership and explicitly enables it.
                _published = true;
                _restorationPending = true;
                if (!_proc.CompareExchangePointer(SlotAddress, _original, WrapperAddress, out var observed, rollbackOnAbandon: true))
                    throw new InvalidOperationException(L10n.T("Runtime.Fov.SetterChanged", observed));
                IsInstalled = true;
                if (!_proc.WriteData(_region + EnabledOffset, new byte[] { 1 }))
                    throw new InvalidOperationException(L10n.T("Runtime.Fov.EnableFailed"));
                log?.Invoke(L10n.T("Runtime.Fov.Enabled", delta, SlotAddress - _build.AssemblyBase, _original - _build.UnityBase));
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log?.Invoke(L10n.T("Runtime.Fov.InstallFailed", LastError));
                if (IsInstalled) Remove();
                if (_region != 0 && !_published) { _proc.Free(_region); _region = 0; }
                LastError ??= ex.Message;
                return false;
            }
        }

        public bool WriteDelta(float delta)
        {
            if (!float.IsFinite(delta) || Math.Abs(delta) > 60 || _region == 0)
            { LastError = L10n.T("Runtime.Fov.WrapperNotReady"); return false; }
            if (!_proc.WriteData(_region + DeltaOffset, BitConverter.GetBytes(delta)))
            { LastError = L10n.T("Runtime.Fov.DeltaWriteFailed"); return false; }
            Delta = delta;
            if (IsInstalled) _baselineCount = _proc.ReadUInt64(_region + CounterOffset);
            return true;
        }

        public FovStatistics ReadStatistics()
        {
            if (_region == 0) return new(0, 0, 0, 0, Delta);
            var data = _proc.Read(_region, 40);
            if (data == null) throw new InvalidOperationException(L10n.T("Runtime.Fov.StatisticsReadFailed"));
            ulong count = BitConverter.ToUInt64(data, CounterOffset);
            return new(count - _baselineCount, BitConverter.ToUInt64(data, CameraOffset),
                BitConverter.ToSingle(data, InputOffset), BitConverter.ToSingle(data, OutputOffset),
                BitConverter.ToSingle(data, DeltaOffset),
                _build.ReadStoredCameraFov(_proc, BitConverter.ToUInt64(data, CameraOffset)));
        }

        public bool Remove(Action<string> log = null)
        {
            LastError = null;
            if (_region == 0) return true;
            if (!_proc.IsAlive)
            {
                IsInstalled = false; _restorationPending = false; Delta = 0;
                return true;
            }
            // Exact pass-through: skip the float instruction entirely, including -0/NaN.
            if (!_proc.WriteData(_region + EnabledOffset, new byte[] { 0 }) && _proc.IsAlive)
            { LastError = L10n.T("Runtime.Fov.DisableFailed"); return false; }
            try
            {
                if (IsInstalled || _proc.HasPendingPointerExchange) _restorationPending = true;
                if (_proc.HasPendingPointerExchange)
                {
                    _proc.ReconcilePointerExchange();
                    IsInstalled = _proc.IsAlive && _proc.ReadUInt64(SlotAddress) == WrapperAddress;
                }
                else if (!IsInstalled && _published && _proc.IsAlive)
                    IsInstalled = _proc.ReadUInt64(SlotAddress) == WrapperAddress;
                if (!IsInstalled)
                {
                    if (_restorationPending && _proc.IsAlive)
                    {
                        ulong current = _proc.ReadUInt64(SlotAddress);
                        if (current != _original)
                            throw new InvalidOperationException(L10n.T("Runtime.Fov.SetterStillOwned", current));
                    }
                    _restorationPending = false; Delta = 0;
                    return true;
                }
                if (_proc.IsAlive)
                {
                    if (!_proc.CompareExchangePointer(SlotAddress, WrapperAddress, _original, out var observed)
                        && observed != _original)
                    {
                        LastError = L10n.T("Runtime.Fov.SetterOwned", observed);
                        IsInstalled = false;
                        log?.Invoke(LastError);
                        return false;
                    }
                    if (_proc.ReadUInt64(SlotAddress) != _original)
                        throw new InvalidOperationException(L10n.T("Runtime.Fov.ReadbackMismatch"));
                }
                IsInstalled = false;
                _restorationPending = false;
                Delta = 0;
                log?.Invoke(L10n.T("Runtime.Fov.Removed"));
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; log?.Invoke(L10n.T("Runtime.Fov.RestoreFailed", LastError)); return false; }
        }

        public void Dispose()
        {
            if (!Remove())
                throw new InvalidOperationException(LastError ?? L10n.T("Runtime.Fov.RestorationPending"));
            // A game thread may already have loaded the old pointer. Its reachable wrapper
            // and state remain valid until game exit (8 KiB per session), even after restore.
            if (_region != 0 && !_published) { _proc.Free(_region); _region = 0; }
        }

        internal static byte[] BuildWrapper(ulong codeAddress, ulong dataAddress)
        {
            var code = new List<byte>(80);
            void Rip(ulong address, byte[] opcode, byte[] suffix = null)
            {
                code.AddRange(opcode);
                long next = checked((long)codeAddress + code.Count + 4 + (suffix?.Length ?? 0));
                int displacement = checked((int)(checked((long)address) - next));
                code.AddRange(BitConverter.GetBytes(displacement));
                if (suffix != null) code.AddRange(suffix);
            }
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x1E, 0xFA }); // ENDBR64
            // Win64 does not preserve arithmetic flags across a call, and the verified
            // original accessor starts with SUB RSP. Leave RSP untouched for correct leaf unwinding.
            Rip(dataAddress + EnabledOffset, new byte[] { 0x80, 0x3D }, new byte[] { 0 }); // cmp byte [enabled],0
            code.Add(0x74); int branchOffset = code.Count; code.Add(0); // je bypass
            Rip(dataAddress + CameraOffset, new byte[] { 0x48, 0x89, 0x0D }); // save RCX
            Rip(dataAddress + InputOffset, new byte[] { 0xF3, 0x0F, 0x11, 0x0D }); // save XMM1
            Rip(dataAddress + DeltaOffset, new byte[] { 0xF3, 0x0F, 0x58, 0x0D }); // addss XMM1,delta
            Rip(dataAddress + OutputOffset, new byte[] { 0xF3, 0x0F, 0x11, 0x0D });
            Rip(dataAddress + CounterOffset, new byte[] { 0xF0, 0x48, 0xFF, 0x05 }); // lock inc qword
            code[branchOffset] = checked((byte)(code.Count - (branchOffset + 1)));
            Rip(dataAddress + OriginalOffset, new byte[] { 0xFF, 0x25 }); // jmp qword [original]
            return code.ToArray();
        }
    }
}

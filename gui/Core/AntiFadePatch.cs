using System;
using System.Collections.Generic;

namespace StarRailUnlocker.Core
{
    public sealed record AntiFadeStatistics(ulong CameraCalls, ulong OtherCalls, ulong Object,
        float Input, float Output, bool Enabled);

    /// <summary>
    /// Modifies only the camera kind's input scalar and tail-forwards the complete original
    /// priority implementation. Ordinary transitions, return values and callback paths survive.
    /// Allocations that might have been published remain valid until the target exits.
    /// </summary>
    public sealed class AntiFadePatcher : IDisposable
    {
        internal const int EnabledOffset = 0, OneOffset = 4, CameraCallsOffset = 8, OtherCallsOffset = 16;
        internal const int ObjectOffset = 24, InputOffset = 32, OutputOffset = 36, GatewayPointerOffset = 48;
        private const int AllocationSize = 0x2000, CodeOffset = 0x1000, GatewayOffset = 0x1100;
        private static readonly byte[] Prologue = { 0x41, 0x56, 0x56, 0x57, 0x55, 0x53, 0x48, 0x83, 0xEC, 0x50 };
        private readonly TargetProcess _proc;
        private readonly ResolvedGameBuild _build;
        private ResolvedCameraFade _resolved;
        private ExecutableEntryPatch _entryPatch;
        private RemoteFunctionTable _unwind;
        private ulong _region, _entry, _cameraBaseline, _otherBaseline;
        private bool _published, _enabled, _disposed;
        public bool IsInstalled => _enabled && _entryPatch?.IsInstalled == true;
        public string LastError { get; private set; }
        public ulong WrapperAddress => _region == 0 ? 0 : checked(_region + CodeOffset);
        public ulong GatewayAddress => _region == 0 ? 0 : checked(_region + GatewayOffset);
        public ulong EntryAddress => _entry;

        public AntiFadePatcher(TargetProcess proc, ResolvedGameBuild build)
        {
            _proc = proc ?? throw new ArgumentNullException(nameof(proc));
            _build = build ?? throw new ArgumentNullException(nameof(build));
        }

        public bool Install(Action<string> log = null)
        {
            LastError = null;
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AntiFadePatcher));
                if (!_proc.IsAlive) throw new InvalidOperationException("游戏进程已退出");
                if (IsInstalled)
                {
                    RequireOwnedEntry();
                    return true;
                }
                if (_entryPatch?.WasPublished == true && !Remove(log)) return false;
                _resolved ??= CameraFadeResolver.Resolve(_build.AssemblyImage, log);
                CameraFadeResolver.VerifyLive(_proc, _build, _resolved);
                _entry = checked(_build.AssemblyBase + _resolved.Helper.Rva);
                if (!_resolved.Helper.Code.AsSpan(0, Prologue.Length).SequenceEqual(Prologue))
                    throw new NotSupportedException("相机过渡入口指令不受支持");
                if (_region == 0) PrepareRegion();
                _unwind ??= new RemoteFunctionTable(_proc, GatewayAddress);
                try { _unwind.Register(); }
                finally { _published |= _unwind.MustRetainGateway; }
                _entryPatch ??= new ExecutableEntryPatch(_proc, _entry, Prologue);
                _cameraBaseline = _proc.ReadUInt64(_region + CameraCallsOffset);
                _otherBaseline = _proc.ReadUInt64(_region + OtherCallsOffset);
                bool installed;
                try { installed = _entryPatch.Install(WrapperAddress, log); }
                finally { _published |= _entryPatch.WasPublished; }
                if (!installed) throw new InvalidOperationException(_entryPatch.LastError ?? "无法安装相机过渡入口");
                RequireOwnedEntry();
                bool wroteEnabled = _proc.WriteData(_region + EnabledOffset, new byte[] { 1 });
                if (wroteEnabled) _enabled = true;
                if (!wroteEnabled || _proc.Read(_region + EnabledOffset, 1) is not { } enabled || enabled[0] != 1)
                    throw new InvalidOperationException("无法启用反虚化标量包装代码");
                log?.Invoke("反虚化已启用：仅相机距离与仰角虚化使用 1.0，普通角色过渡继续执行");
                return true;
            }
            catch (Exception ex)
            {
                string reason = ex.Message;
                bool disabled = Disable();
                // Installation can have written the entry even when protection restoration failed.
                if (disabled && _entryPatch?.WasPublished == true)
                {
                    bool removed = _entryPatch.Remove(log);
                    if (!removed) reason += "；入口还原待重试：" + _entryPatch.LastError;
                }
                if (!disabled && _proc.IsAlive) reason += "；无法确认包装代码已停用";
                LastError = reason;
                log?.Invoke("反虚化安装失败：" + reason);
                return false;
            }
        }

        private void PrepareRegion()
        {
            _region = _proc.AllocateNear(_entry, AllocationSize);
            if (_region == 0) throw new InvalidOperationException("无法分配相机过渡包装代码");
            try
            {
                // AllocateNear can fall back to an arbitrary address; all relative branches must fit.
                _ = ExecutableEntryPatch.BuildEntryWord(_entry, WrapperAddress);
                byte[] gateway = BuildGateway(GatewayAddress, _entry, Prologue);
                var flags = new List<ulong>();
                foreach (ulong rva in _resolved.HotfixFlags) flags.Add(checked(_build.AssemblyBase + rva));
                byte[] wrapper = BuildWrapper(WrapperAddress, _region, flags);
                if (wrapper.Length > GatewayOffset - CodeOffset || gateway.Length > 0x100)
                    throw new InvalidOperationException("包装代码超出独立代码页边界");
                var state = new byte[0x1000];
                BitConverter.GetBytes(1.0f).CopyTo(state, OneOffset);
                BitConverter.GetBytes(GatewayAddress).CopyTo(state, GatewayPointerOffset);
                if (!_proc.WriteData(_region, state) || !_proc.WriteData(WrapperAddress, wrapper)
                    || !_proc.WriteData(GatewayAddress, gateway))
                    throw new InvalidOperationException("无法初始化反虚化代码与状态");
                CameraFadeResolver.Equal(_proc, _region, state);
                CameraFadeResolver.Equal(_proc, WrapperAddress, wrapper);
                CameraFadeResolver.Equal(_proc, GatewayAddress, gateway);
                if (!_proc.MakeExecutable(WrapperAddress, 0x1000))
                    throw new InvalidOperationException("无法完成反虚化代码页保护");
            }
            catch
            {
                // No entry has referenced this allocation, so this is the only safe eager free.
                _proc.Free(_region); _region = 0;
                throw;
            }
        }

        private void RequireOwnedEntry()
        {
            byte[] current = _proc.Read(_entry, Prologue.Length);
            byte[] owned = ExecutableEntryPatch.BuildEntryWord(_entry, WrapperAddress);
            if (current == null || !current.AsSpan(0, owned.Length).SequenceEqual(owned)
                || !current.AsSpan(owned.Length).SequenceEqual(Prologue.AsSpan(owned.Length)))
                throw new InvalidOperationException("相机入口已由其他代码接管；未启用或覆盖该入口");
        }

        private bool Disable()
        {
            if (_region == 0 || !_proc.IsAlive) { _enabled = false; return true; }
            bool disabled = _proc.WriteData(_region + EnabledOffset, new byte[] { 0 })
                && _proc.Read(_region + EnabledOffset, 1) is { } state && state[0] == 0;
            if (disabled) _enabled = false;
            return disabled;
        }

        public bool Remove(Action<string> log = null)
        {
            LastError = null;
            try
            {
                if (!Disable()) throw new InvalidOperationException("无法停用反虚化包装代码，入口保持有效以便重试");
                if (_region == 0 || !_proc.IsAlive) return true;
                if (_entryPatch != null)
                {
                    _published |= _entryPatch.WasPublished;
                    if (!_entryPatch.Remove(log)) throw new InvalidOperationException(_entryPatch.LastError ?? "无法还原相机入口");
                }
                // A failed partial publication is never silently reported as a completed removal.
                if (_entry != 0 && _published)
                    CameraFadeResolver.Equal(_proc, _entry, Prologue);
                log?.Invoke("反虚化已停用，相机过渡入口已还原");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log?.Invoke("反虚化还原失败：" + LastError);
                return false;
            }
        }

        public AntiFadeStatistics GetStatistics()
        {
            if (_region == 0 || !_proc.IsAlive) return new(0, 0, 0, 0, 0, false);
            byte[] data = _proc.Read(_region, 56);
            if (data == null) throw new InvalidOperationException("无法读取反虚化调用统计");
            return new(unchecked(BitConverter.ToUInt64(data, CameraCallsOffset) - _cameraBaseline),
                unchecked(BitConverter.ToUInt64(data, OtherCallsOffset) - _otherBaseline),
                BitConverter.ToUInt64(data, ObjectOffset), BitConverter.ToSingle(data, InputOffset),
                BitConverter.ToSingle(data, OutputOffset), _enabled && data[EnabledOffset] != 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!Remove())
                throw new InvalidOperationException(LastError ?? "相机入口尚未还原，保留补丁状态以便重试");
            _unwind?.Dispose();
            _published |= _unwind?.MustRetainGateway == true;
            _published |= _entryPatch?.WasPublished == true;
            if (_region != 0 && !_published) { _proc.Free(_region); _region = 0; }
            _disposed = true;
        }

        /// <summary>Changes XMM1 only for enabled, unhotfixed camera-kind calls. RSP and all GPRs survive.</summary>
        public static byte[] BuildWrapper(ulong codeAddress, ulong dataAddress, IReadOnlyList<ulong> hotfixFlags = null)
        {
            var code = new List<byte>(192); var exits = new List<int>();
            void Rip(ulong address, byte[] opcode, byte[] suffix = null)
            {
                code.AddRange(opcode);
                long next = checked((long)codeAddress + code.Count + 4 + (suffix?.Length ?? 0));
                code.AddRange(BitConverter.GetBytes(checked((int)(checked((long)address) - next))));
                if (suffix != null) code.AddRange(suffix);
            }
            int Branch(byte condition)
            {
                code.Add(0x0F); code.Add(condition); int at = code.Count; code.AddRange(new byte[4]); return at;
            }
            // Resolve branch displacements after all instructions have been appended.
            void Patch(int at, int target)
            {
                byte[] displacement = BitConverter.GetBytes(checked(target - (at + 4)));
                for (int i = 0; i < 4; i++) code[at + i] = displacement[i];
            }
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x1E, 0xFA }); // ENDBR64; leaf wrapper
            Rip(dataAddress + EnabledOffset, new byte[] { 0x80, 0x3D }, new byte[] { 0 });
            exits.Add(Branch(0x84)); // je tail
            if (hotfixFlags != null)
                foreach (ulong flag in hotfixFlags)
                {
                    Rip(flag, new byte[] { 0x80, 0x3D }, new byte[] { 0 });
                    exits.Add(Branch(0x85)); // jne tail: a later live hotfix disables modification
                }
            code.AddRange(new byte[] { 0x41, 0x83, 0xF8, 0x01 }); // cmp r8d,1
            int other = Branch(0x85);
            Rip(dataAddress + ObjectOffset, new byte[] { 0x48, 0x89, 0x0D });
            Rip(dataAddress + InputOffset, new byte[] { 0xF3, 0x0F, 0x11, 0x0D });
            Rip(dataAddress + OneOffset, new byte[] { 0xF3, 0x0F, 0x10, 0x0D }); // movss xmm1,1.0
            Rip(dataAddress + OutputOffset, new byte[] { 0xF3, 0x0F, 0x11, 0x0D });
            Rip(dataAddress + CameraCallsOffset, new byte[] { 0xF0, 0x48, 0xFF, 0x05 });
            code.Add(0xE9); int afterCamera = code.Count; code.AddRange(new byte[4]);
            Patch(other, code.Count);
            Rip(dataAddress + OtherCallsOffset, new byte[] { 0xF0, 0x48, 0xFF, 0x05 });
            int tail = code.Count;
            Patch(afterCamera, tail);
            foreach (int at in exits) Patch(at, tail);
            Rip(dataAddress + GatewayPointerOffset, new byte[] { 0xFF, 0x25 });
            return code.ToArray();
        }

        public static byte[] BuildGateway(ulong gatewayAddress, ulong entryAddress, byte[] originalInstructions)
        {
            if (originalInstructions == null || !originalInstructions.AsSpan().SequenceEqual(Prologue))
                throw new NotSupportedException("Only the verified complete stack-save prologue can be displaced");
            // An outward JMP is scanned as a tail epilogue and skips this frame's unwind codes.
            // CMP RSP,RSP; JE always transfers without being recognized as an epilogue.
            var code = new byte[Prologue.Length + 9]; Prologue.CopyTo(code, 0);
            new byte[] { 0x48, 0x39, 0xE4, 0x0F, 0x84 }.CopyTo(code, Prologue.Length);
            long next = checked((long)gatewayAddress + code.Length);
            BitConverter.GetBytes(checked((int)(checked((long)entryAddress + Prologue.Length) - next))).CopyTo(code, Prologue.Length + 5);
            return code;
        }
    }
}

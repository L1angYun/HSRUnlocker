using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StarRailUnlocker.Core
{
    public enum SessionState { Idle, WaitingForGame, Patching, Active, Failed, GameExited }

    /// <summary>One worker owns all target memory and hook changes, including teardown.</summary>
    public sealed class UnlockSession : IDisposable
    {
        public UnlockConfig Config { get; }
        public SessionState State { get; private set; } = SessionState.Idle;
        public string LastMessage { get; private set; } = "";
        public ulong CapAddress { get; private set; }
        public ulong CapRva { get; private set; }
        public int OriginalValue { get; private set; }
        public int AppliedValue { get; private set; }
        public int ReapplyCount { get; private set; }
        public uint Pid { get; private set; }
        public FovStatistics FovStatistics { get; private set; }
        public bool FovInstalled { get; private set; }
        public string FovError { get; private set; } = "";
        public bool AntiFadeInstalled { get; private set; }
        public string AntiFadeError { get; private set; } = "";
        // A null message refreshes feature snapshots only; LastMessage keeps the last diagnostic.
        public event Action<SessionState, string> Changed;
        private static readonly TimeSpan FeatureRetryInterval = TimeSpan.FromSeconds(5);
        private readonly object _lifecycle = new();
        private readonly HashSet<string> _fovMessages = new(StringComparer.Ordinal);
        private readonly HashSet<string> _antiFadeMessages = new(StringComparer.Ordinal);
        private TargetProcess _proc;
        private ResolvedGameBuild _build;
        private CancellationTokenSource _cts;
        private Task _worker;
        private FovPatcher _fov;
        private AntiFadePatcher _antiFade;
        private int _fovRevision, _antiFadeRevision;
        private int _seenFovRevision = -1, _seenAntiFadeRevision = -1;
        private bool _capWritten, _fovReported;
        private float _lastRequestedDelta = float.NaN;
        private bool _lastRequestedAntiFade;
        private DateTime _nextFovRetry, _nextAntiFadeRetry, _nextFovReport;
        private bool _retryFov, _retryAntiFade, _fovNeedsRemoval, _antiFadeNeedsRemoval;

        public UnlockSession(UnlockConfig cfg) { Config = cfg; }
        private void Set(SessionState state, string message)
        {
            State = state; LastMessage = message;
            Changed?.Invoke(state, message);
        }
        private void Log(string message) => Set(State, message);
        private void SafeSet(SessionState state, string message)
        {
            try { Set(state, message); }
            catch { /* Optional feature diagnostics must not interrupt FPS or teardown. */ }
        }
        private void SafeLog(string message) => SafeSet(State, message);
        private bool FeatureLog(HashSet<string> messages, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || !messages.Add(message)) return false;
            SafeLog(message);
            return true;
        }
        private void LogFov(string message) => FeatureLog(_fovMessages, message);
        private void LogAntiFade(string message) => FeatureLog(_antiFadeMessages, message);
        private void FeatureReport(HashSet<string> messages, string message)
        {
            if (FeatureLog(messages, message)) return;
            // Recovery still refreshes snapshots even when its diagnostic was already logged.
            try { Changed?.Invoke(State, null); }
            catch { /* A status subscriber must not interrupt optional features or teardown. */ }
        }
        private void ReportFov(string message) => FeatureReport(_fovMessages, message);
        private void ReportAntiFade(string message) => FeatureReport(_antiFadeMessages, message);
        private static string ErrorOr(string error, string fallback) =>
            string.IsNullOrWhiteSpace(error) ? fallback : error;
        public bool IsRunning => _worker != null && !_worker.IsCompleted;

        public void Start(uint pid = 0, bool launchIfMissing = false)
        {
            lock (_lifecycle)
            {
                if (IsRunning) return;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _worker = Task.Run(() => Run(pid, launchIfMissing, token));
            }
        }
        public void Stop() { lock (_lifecycle) _cts?.Cancel(); }
        // UI handlers only queue work. They never race the worker's Install/Remove/Dispose.
        public void ApplyFov() => Interlocked.Increment(ref _fovRevision);
        public void ApplyAntiFade() => Interlocked.Increment(ref _antiFadeRevision);
        private static void Delay(CancellationToken token, int milliseconds)
        {
            if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
        }
        private void ResetFeatures()
        {
            FovStatistics = null;
            FovInstalled = AntiFadeInstalled = false;
            FovError = AntiFadeError = "";
            _seenFovRevision = _seenAntiFadeRevision = -1;
            _lastRequestedDelta = float.NaN;
            _lastRequestedAntiFade = false;
            _retryFov = _retryAntiFade = _fovNeedsRemoval = _antiFadeNeedsRemoval = false;
            _fovReported = false;
            _fovMessages.Clear(); _antiFadeMessages.Clear();
        }

        private void Run(uint pid, bool launchIfMissing, CancellationToken token)
        {
            bool failure = false;
            string failureMessage = "";
            ResetFeatures();
            try
            {
                if (pid == 0)
                {
                    Set(SessionState.WaitingForGame, "正在查找 StarRail.exe…");
                    bool launchRequested = launchIfMissing || Config.AutoStart;
                    if (launchRequested)
                    {
                        foreach (uint existing in TargetProcess.FindProcessIds("StarRail.exe"))
                        {
                            var module = TargetProcess.FindMainModule(existing);
                            if (string.IsNullOrWhiteSpace(module.Path))
                                throw new InvalidOperationException($"无法确认已运行 StarRail.exe（PID {existing}）的客户端路径；请使用可访问该进程的权限附加，未启动另一个客户端");
                            if (string.Equals(System.IO.Path.GetFullPath(module.Path), System.IO.Path.GetFullPath(Config.ProcessPath), StringComparison.OrdinalIgnoreCase))
                            { pid = existing; break; }
                        }
                    }
                    else pid = TargetProcess.FindProcessId("StarRail.exe");
                    if (pid == 0 && launchRequested)
                    {
                        pid = GameLauncher.Launch(Config, Log, token);
                        Log($"已启动游戏，PID {pid}" + (Config.UseMobileUI ? "（移动 UI 入口已应用）" : ""));
                    }
                    else if (pid != 0 && Config.UseMobileUI)
                        Log("移动 UI 只在启动游戏时应用；当前附加模式不会改变已运行客户端的启动参数");
                    var deadline = DateTime.UtcNow.AddMinutes(10);
                    while (pid == 0 && DateTime.UtcNow < deadline)
                    {
                        Delay(token, 500);
                        pid = TargetProcess.FindProcessId("StarRail.exe");
                    }
                    if (pid == 0) throw new InvalidOperationException("等待超时，未出现游戏进程");
                }
                token.ThrowIfCancellationRequested();
                Pid = pid;
                _proc = new TargetProcess(pid);
                Set(SessionState.Patching, $"已附加到 PID {pid}，等待游戏模块加载…");
                var moduleDeadline = DateTime.UtcNow.AddMinutes(3);
                while (TargetProcess.FindModule(pid, "UnityPlayer.dll").Base == 0
                    || TargetProcess.FindModule(pid, "GameAssembly.dll").Base == 0)
                {
                    if (!_proc.IsAlive) throw new InvalidOperationException("游戏在加载过程中退出");
                    if (DateTime.UtcNow >= moduleDeadline) throw new InvalidOperationException("等待游戏模块加载超时");
                    Delay(token, 250);
                }
                _build = GameBuildResolver.Resolve(_proc, Log);
                var cap = FrameCapLocator.Resolve(_proc, _build, Log);
                CapRva = cap.Rva; CapAddress = _build.UnityBase + cap.Rva;
                OriginalValue = cap.LiveValue;
                AppliedValue = Math.Clamp(Config.TargetFramerate, 10, 480);
                if (!_proc.WriteInt32(CapAddress, AppliedValue)) throw new InvalidOperationException("写入帧率上限失败");
                _capWritten = true;
                if (_proc.ReadInt32(CapAddress) != AppliedValue) throw new InvalidOperationException("帧率上限写入后读回不一致");
                Set(SessionState.Active, $"帧率上限 {OriginalValue} → {AppliedValue}（UnityPlayer+0x{CapRva:X}）");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_proc.IsAlive) { Set(SessionState.GameExited, "游戏已退出"); break; }
                    ProcessFov();
                    ReadFovStatistics();
                    ProcessAntiFade();
                    int desired = Math.Clamp(Config.TargetFramerate, 10, 480);
                    int current = _proc.ReadInt32(CapAddress);
                    if (current == int.MinValue) throw new InvalidOperationException("帧率上限已无法读取");
                    if (desired != AppliedValue || current != desired)
                    {
                        if (current != AppliedValue && current >= -1 && current <= 1000)
                            OriginalValue = current;
                        if (!_proc.WriteInt32(CapAddress, desired)) throw new InvalidOperationException("更新帧率上限失败");
                        if (desired == AppliedValue) ReapplyCount++;
                        AppliedValue = desired;
                        Log($"游戏把上限改了回去，已恢复为 {desired}（第 {ReapplyCount} 次）");
                    }
                    Delay(token, 200);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                failure = true; failureMessage = ex.Message;
                SafeSet(SessionState.Failed, failureMessage);
            }
            finally
            {
                bool restored, exited, pendingReported = false;
                while (true)
                {
                    restored = CleanupAntiFade();
                    restored &= CleanupFov();
                    restored &= CleanupFrameCap();
                    exited = _proc != null && !_proc.IsAlive;
                    if (restored || exited || _proc == null) break;
                    if (!pendingReported)
                    {
                        pendingReported = true;
                        SafeSet(SessionState.Failed, "还原尚未完成，正在保留目标资源并重试；完成还原或游戏退出后才能结束会话");
                    }
                    // Cancellation requests restoration; it cannot abandon a published patch.
                    Delay(CancellationToken.None, 1000);
                }
                if (exited)
                {
                    _antiFade = null; _fov = null;
                    AntiFadeInstalled = FovInstalled = false;
                    AntiFadeError = FovError = "";
                    FovStatistics = null;
                }
                try { _proc?.Dispose(); }
                catch (Exception ex) { restored = false; SafeLog("进程资源清理失败：" + ex.Message); }
                finally
                {
                    _proc = null; _build = null; _capWritten = false;
                }
                if (exited) SafeSet(SessionState.GameExited, "游戏已退出，目标资源已释放");
                else if (failure) SafeSet(SessionState.Failed, failureMessage);
                else if (State != SessionState.GameExited)
                    SafeSet(restored ? SessionState.Idle : SessionState.Failed,
                        restored ? "已停止：帧率、FOV 与反虚化已还原" : "已停止，但还原不完整；详情见日志");
            }
        }

        private void ProcessFov()
        {
            int revision = Volatile.Read(ref _fovRevision);
            float delta = (float)Config.FovDelta;
            bool changed = revision != _seenFovRevision || !delta.Equals(_lastRequestedDelta);
            if (!changed && (!_retryFov || DateTime.UtcNow < _nextFovRetry)) return;
            if (changed)
            {
                _fovMessages.Clear();
                FovStatistics = null; _fovReported = false;
            }
            _seenFovRevision = revision; _lastRequestedDelta = delta; _retryFov = false;
            try
            {
                if (!float.IsFinite(delta) || Math.Abs(delta) > 60)
                    throw new InvalidOperationException("FOV 增量必须是 −60…60 之间的有效数值");
                if (delta == 0)
                {
                    if (!TryRemoveFov(out var error)) { RefuseFov("还原失败：" + error, false); return; }
                    FovError = ""; FovStatistics = null;
                    ReportFov("FOV 未启用；已还原的视野将在游戏下次更新镜头时恢复");
                    return;
                }
                if (_fovNeedsRemoval && !TryRemoveFov(out var pendingError))
                { RefuseFov("上次还原尚未完成：" + pendingError, false); return; }
                if (!_build.TryResolveCameraSlots(_proc, LogFov))
                {
                    if (!TryRemoveFov(out var error)) { RefuseFov("还原失败：" + error, false); return; }
                    FovError = ""; FovStatistics = null;
                    _retryFov = true; _nextFovRetry = DateTime.UtcNow + FeatureRetryInterval;
                    ReportFov("FOV 等待游戏初始化相机接口；帧率解锁不受影响");
                    return;
                }
                _fov ??= new FovPatcher(_proc, _build);
                bool alreadyInstalled = _fov.IsInstalled;
                // Install only verifies on its first allocation; reused wrappers need this too.
                if (!alreadyInstalled) _build.VerifyCameraAccessors(_proc);
                bool ok = alreadyInstalled ? _fov.WriteDelta(delta) : _fov.Install(delta, LogFov);
                if (!ok || !_fov.IsInstalled)
                { RefuseFov(ErrorOr(_fov.LastError, "无法安装或更新 FOV")); return; }
                FovInstalled = true; FovError = "";
                FovStatistics = null; _fovReported = false; _nextFovReport = DateTime.UtcNow;
                ReportFov($"FOV 增量已应用为 {delta:0.##}，等待游戏更新镜头");
            }
            catch (Exception ex) { RefuseFov(ex.Message); }
        }

        private void ReadFovStatistics()
        {
            if (_fov == null || !FovInstalled || !string.IsNullOrEmpty(FovError)
                || DateTime.UtcNow < _nextFovReport) return;
            _nextFovReport = DateTime.UtcNow.AddSeconds(1);
            try
            {
                FovStatistics = _fov.ReadStatistics();
                if (FovStatistics.Calls > 0 && !_fovReported)
                {
                    _fovReported = true;
                    LogFov($"FOV 已生效：{FovStatistics.Input:0.###} → {FovStatistics.Output:0.###}" +
                        $"（存储 {FovStatistics.StoredFov?.ToString("0.###") ?? "未读到"}，增量 {FovStatistics.Delta:0.##}，" +
                        $"累计 {FovStatistics.Calls} 次）");
                }
            }
            catch (Exception ex) { RefuseFov("读取镜头状态失败：" + ex.Message); }
        }

        private void ProcessAntiFade()
        {
            int revision = Volatile.Read(ref _antiFadeRevision);
            bool desired = Config.DisableCameraFade;
            bool changed = revision != _seenAntiFadeRevision || desired != _lastRequestedAntiFade;
            if (!changed && (!_retryAntiFade || DateTime.UtcNow < _nextAntiFadeRetry)) return;
            if (changed) _antiFadeMessages.Clear();
            _seenAntiFadeRevision = revision; _lastRequestedAntiFade = desired; _retryAntiFade = false;
            try
            {
                if (!desired)
                {
                    if (!TryRemoveAntiFade(out var error)) { RefuseAntiFade("还原失败：" + error, false); return; }
                    AntiFadeError = "";
                    ReportAntiFade("反虚化未启用，相机淡出保持原样");
                    return;
                }
                if (_antiFadeNeedsRemoval && !TryRemoveAntiFade(out var pendingError))
                { RefuseAntiFade("上次还原尚未完成：" + pendingError, false); return; }
                // Discovery/constructor failures are optional-feature failures, never FPS failures.
                _antiFade ??= new AntiFadePatcher(_proc, _build);
                bool ok = _antiFade.IsInstalled || _antiFade.Install(LogAntiFade);
                if (!ok || !_antiFade.IsInstalled)
                { RefuseAntiFade(ErrorOr(_antiFade.LastError, "无法启用相机反虚化")); return; }
                AntiFadeInstalled = true; AntiFadeError = "";
                ReportAntiFade("反虚化已应用，效果待游戏场景验证");
            }
            catch (Exception ex) { RefuseAntiFade(ex.Message); }
        }

        private void RefuseFov(string error, bool attemptRemove = true)
        {
            string detail = ErrorOr(error, "无法安全应用 FOV");
            if (attemptRemove && !TryRemoveFov(out var removalError))
                detail += "；还原未完成：" + removalError;
            FovError = detail; FovStatistics = null; _fovReported = false;
            _retryFov = true; _nextFovRetry = DateTime.UtcNow + FeatureRetryInterval;
            ReportFov("FOV 暂不可用：" + detail + "；帧率解锁不受影响，将稍后重试");
        }
        private void RefuseAntiFade(string error, bool attemptRemove = true)
        {
            string detail = ErrorOr(error, "无法安全应用反虚化");
            if (attemptRemove && !TryRemoveAntiFade(out var removalError))
                detail += "；还原未完成：" + removalError;
            AntiFadeError = detail;
            _retryAntiFade = true; _nextAntiFadeRetry = DateTime.UtcNow + FeatureRetryInterval;
            ReportAntiFade("反虚化暂不可用：" + detail + "；帧率解锁不受影响，将稍后重试");
        }

        private bool TryRemoveFov(out string error)
        {
            error = "";
            if (_fov == null) { FovInstalled = false; _fovNeedsRemoval = false; return true; }
            try
            {
                bool ok = _fov.Remove(LogFov);
                FovInstalled = _fov.IsInstalled;
                if (ok && !FovInstalled) { _fovNeedsRemoval = false; return true; }
                error = ErrorOr(_fov.LastError, "FOV 尚未完全还原");
            }
            catch (Exception ex) { error = ex.Message; }
            _fovNeedsRemoval = true;
            return false;
        }
        private bool TryRemoveAntiFade(out string error)
        {
            error = "";
            if (_antiFade == null) { AntiFadeInstalled = false; _antiFadeNeedsRemoval = false; return true; }
            try
            {
                bool ok = _antiFade.Remove(LogAntiFade);
                AntiFadeInstalled = _antiFade.IsInstalled;
                if (ok && !AntiFadeInstalled) { _antiFadeNeedsRemoval = false; return true; }
                error = ErrorOr(_antiFade.LastError, "反虚化尚未完全还原");
            }
            catch (Exception ex) { error = ex.Message; }
            _antiFadeNeedsRemoval = true;
            return false;
        }

        private bool CleanupAntiFade()
        {
            bool restored = TryRemoveAntiFade(out var error);
            if (!restored)
            {
                AntiFadeError = error; ReportAntiFade("反虚化还原未完成：" + error);
                return false;
            }
            if (_antiFade != null)
            {
                try
                {
                    _antiFade.Dispose(); AntiFadeInstalled = _antiFade.IsInstalled;
                    _antiFade = null;
                }
                catch (Exception ex)
                { restored = false; AntiFadeError = ex.Message; LogAntiFade("反虚化资源清理失败：" + ex.Message); }
            }
            if (restored) AntiFadeError = "";
            return restored;
        }
        private bool CleanupFov()
        {
            FovStatistics = null;
            bool restored = TryRemoveFov(out var error);
            if (!restored)
            {
                FovError = error; ReportFov("FOV 还原未完成：" + error);
                return false;
            }
            if (_fov != null)
            {
                try
                {
                    _fov.Dispose(); FovInstalled = _fov.IsInstalled;
                    _fov = null;
                }
                catch (Exception ex)
                { restored = false; FovError = ex.Message; LogFov("FOV 资源清理失败：" + ex.Message); }
            }
            if (restored) FovError = "";
            return restored;
        }

        private bool CleanupFrameCap()
        {
            try
            {
                if (!_capWritten || _proc == null || !_proc.IsAlive) return true;
                if (_proc.WriteInt32(CapAddress, OriginalValue)
                    && _proc.ReadInt32(CapAddress) == OriginalValue)
                {
                    _capWritten = false;
                    return true;
                }
                SafeLog("帧率上限未能还原，将继续重试");
            }
            catch (Exception ex) { SafeLog("帧率上限还原失败：" + ex.Message); }
            return false;
        }

        public void Dispose()
        {
            Stop();
            // Do not dispose token/process state while the worker is performing teardown.
            if (_worker != null && Task.CurrentId != _worker.Id) _worker.GetAwaiter().GetResult();
            _cts?.Dispose(); _cts = null;
        }
    }
}

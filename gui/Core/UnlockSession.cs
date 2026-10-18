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
        private UnlockConfig _launchConfig;
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
                _launchConfig = Config.Clone();
                _launchConfig.Normalize();
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
            var launchConfig = _launchConfig ?? Config;
            ResetFeatures();
            try
            {
                if (pid == 0)
                {
                    Set(SessionState.WaitingForGame, L10n.T("Runtime.Session.FindingGame"));
                    bool launchRequested = launchIfMissing || launchConfig.AutoStart;
                    if (launchRequested)
                    {
                        foreach (uint existing in TargetProcess.FindProcessIds("StarRail.exe"))
                        {
                            var module = TargetProcess.FindMainModule(existing);
                            if (string.IsNullOrWhiteSpace(module.Path))
                                throw new InvalidOperationException(L10n.T("Runtime.Session.RunningPathUnknown", existing));
                            if (string.Equals(System.IO.Path.GetFullPath(module.Path), System.IO.Path.GetFullPath(launchConfig.ProcessPath), StringComparison.OrdinalIgnoreCase))
                            { pid = existing; break; }
                        }
                    }
                    else pid = TargetProcess.FindProcessId("StarRail.exe");
                    if (pid == 0 && launchRequested)
                    {
                        pid = GameLauncher.Launch(launchConfig, Log, token);
                        Log(L10n.T(launchConfig.UseMobileUI ? "Runtime.Session.GameStartedMobile" : "Runtime.Session.GameStarted", pid));
                    }
                    else if (pid != 0 && launchConfig.UseMobileUI)
                        Log(L10n.T("Runtime.Session.MobileAttachOnly"));
                    var deadline = DateTime.UtcNow.AddMinutes(10);
                    while (pid == 0 && DateTime.UtcNow < deadline)
                    {
                        Delay(token, 500);
                        pid = TargetProcess.FindProcessId("StarRail.exe");
                    }
                    if (pid == 0) throw new InvalidOperationException(L10n.T("Runtime.Session.GameWaitTimeout"));
                }
                token.ThrowIfCancellationRequested();
                Pid = pid;
                _proc = new TargetProcess(pid);
                Set(SessionState.Patching, L10n.T("Runtime.Session.AttachedWaiting", pid));
                var moduleDeadline = DateTime.UtcNow.AddMinutes(3);
                while (TargetProcess.FindModule(pid, "UnityPlayer.dll").Base == 0
                    || TargetProcess.FindModule(pid, "GameAssembly.dll").Base == 0)
                {
                    if (!_proc.IsAlive) throw new InvalidOperationException(L10n.T("Runtime.Session.ExitDuringLoad"));
                    if (DateTime.UtcNow >= moduleDeadline) throw new InvalidOperationException(L10n.T("Runtime.Session.ModuleWaitTimeout"));
                    Delay(token, 250);
                }
                _build = GameBuildResolver.Resolve(_proc, Log);
                var cap = FrameCapLocator.Resolve(_proc, _build, Log);
                CapRva = cap.Rva; CapAddress = _build.UnityBase + cap.Rva;
                OriginalValue = cap.LiveValue;
                AppliedValue = Math.Clamp(Config.TargetFramerate, 10, 480);
                if (!_proc.WriteInt32(CapAddress, AppliedValue)) throw new InvalidOperationException(L10n.T("Runtime.Session.WriteCapFailed"));
                _capWritten = true;
                if (_proc.ReadInt32(CapAddress) != AppliedValue) throw new InvalidOperationException(L10n.T("Runtime.Session.CapReadbackMismatch"));
                Set(SessionState.Active, L10n.T("Runtime.Session.CapApplied", OriginalValue, AppliedValue, CapRva));
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_proc.IsAlive) { Set(SessionState.GameExited, L10n.T("Runtime.Session.GameExited")); break; }
                    ProcessFov();
                    ReadFovStatistics();
                    ProcessAntiFade();
                    int desired = Math.Clamp(Config.TargetFramerate, 10, 480);
                    int current = _proc.ReadInt32(CapAddress);
                    if (current == int.MinValue) throw new InvalidOperationException(L10n.T("Runtime.Session.CapUnreadable"));
                    if (desired != AppliedValue || current != desired)
                    {
                        if (current != AppliedValue && current >= -1 && current <= 1000)
                            OriginalValue = current;
                        if (!_proc.WriteInt32(CapAddress, desired)) throw new InvalidOperationException(L10n.T("Runtime.Session.UpdateCapFailed"));
                        if (desired == AppliedValue) ReapplyCount++;
                        AppliedValue = desired;
                        Log(L10n.T("Runtime.Session.CapReapplied", desired, ReapplyCount));
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
                        SafeSet(SessionState.Failed, L10n.T("Runtime.Session.RestorationPending"));
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
                catch (Exception ex) { restored = false; SafeLog(L10n.T("Runtime.Session.ProcessCleanupFailed", ex.Message)); }
                finally
                {
                    _proc = null; _build = null; _capWritten = false;
                }
                if (exited) SafeSet(SessionState.GameExited, L10n.T("Runtime.Session.ExitedResourcesReleased"));
                else if (failure) SafeSet(SessionState.Failed, failureMessage);
                else if (State != SessionState.GameExited)
                    SafeSet(restored ? SessionState.Idle : SessionState.Failed,
                        L10n.T(restored ? "Runtime.Session.StoppedRestored" : "Runtime.Session.StoppedIncomplete"));
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
                    throw new InvalidOperationException(L10n.T("Runtime.Fov.InvalidDelta"));
                if (delta == 0)
                {
                    if (!TryRemoveFov(out var error)) { RefuseFov(L10n.T("Runtime.Session.RestoreFailed", error), false); return; }
                    FovError = ""; FovStatistics = null;
                    ReportFov(L10n.T("Runtime.Session.FovDisabled"));
                    return;
                }
                if (_fovNeedsRemoval && !TryRemoveFov(out var pendingError))
                { RefuseFov(L10n.T("Runtime.Session.PreviousRestorePending", pendingError), false); return; }
                if (!_build.TryResolveCameraSlots(_proc, LogFov))
                {
                    if (!TryRemoveFov(out var error)) { RefuseFov(L10n.T("Runtime.Session.RestoreFailed", error), false); return; }
                    FovError = ""; FovStatistics = null;
                    _retryFov = true; _nextFovRetry = DateTime.UtcNow + FeatureRetryInterval;
                    ReportFov(L10n.T("Runtime.Session.FovWaiting"));
                    return;
                }
                _fov ??= new FovPatcher(_proc, _build);
                bool alreadyInstalled = _fov.IsInstalled;
                // Install only verifies on its first allocation; reused wrappers need this too.
                if (!alreadyInstalled) _build.VerifyCameraAccessors(_proc);
                bool ok = alreadyInstalled ? _fov.WriteDelta(delta) : _fov.Install(delta, LogFov);
                if (!ok || !_fov.IsInstalled)
                { RefuseFov(ErrorOr(_fov.LastError, L10n.T("Runtime.Session.FovInstallUpdateFailed"))); return; }
                FovInstalled = true; FovError = "";
                FovStatistics = null; _fovReported = false; _nextFovReport = DateTime.UtcNow;
                ReportFov(L10n.T("Runtime.Session.FovDeltaApplied", delta));
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
                    LogFov(L10n.T("Runtime.Session.FovEffective", FovStatistics.Input, FovStatistics.Output,
                        FovStatistics.StoredFov.HasValue ? (object)FovStatistics.StoredFov.Value
                            : L10n.T("Runtime.Session.ValueUnreadable"),
                        FovStatistics.Delta, FovStatistics.Calls));
                }
            }
            catch (Exception ex) { RefuseFov(L10n.T("Runtime.Session.FovStatisticsFailed", ex.Message)); }
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
                    if (!TryRemoveAntiFade(out var error)) { RefuseAntiFade(L10n.T("Runtime.Session.RestoreFailed", error), false); return; }
                    AntiFadeError = "";
                    ReportAntiFade(L10n.T("Runtime.Session.AntiFadeDisabled"));
                    return;
                }
                if (_antiFadeNeedsRemoval && !TryRemoveAntiFade(out var pendingError))
                { RefuseAntiFade(L10n.T("Runtime.Session.PreviousRestorePending", pendingError), false); return; }
                // Discovery/constructor failures are optional-feature failures, never FPS failures.
                _antiFade ??= new AntiFadePatcher(_proc, _build);
                bool ok = _antiFade.IsInstalled || _antiFade.Install(LogAntiFade);
                if (!ok || !_antiFade.IsInstalled)
                { RefuseAntiFade(ErrorOr(_antiFade.LastError, L10n.T("Runtime.Session.AntiFadeEnableFailed"))); return; }
                AntiFadeInstalled = true; AntiFadeError = "";
                ReportAntiFade(L10n.T("Runtime.Session.AntiFadeApplied"));
            }
            catch (Exception ex) { RefuseAntiFade(ex.Message); }
        }

        private void RefuseFov(string error, bool attemptRemove = true)
        {
            string detail = ErrorOr(error, L10n.T("Runtime.Session.FovUnsafe"));
            if (attemptRemove && !TryRemoveFov(out var removalError))
                detail = L10n.T("Runtime.Session.RemovalIncomplete", detail, removalError);
            FovError = detail; FovStatistics = null; _fovReported = false;
            _retryFov = true; _nextFovRetry = DateTime.UtcNow + FeatureRetryInterval;
            ReportFov(L10n.T("Runtime.Session.FovUnavailable", detail));
        }
        private void RefuseAntiFade(string error, bool attemptRemove = true)
        {
            string detail = ErrorOr(error, L10n.T("Runtime.Session.AntiFadeUnsafe"));
            if (attemptRemove && !TryRemoveAntiFade(out var removalError))
                detail = L10n.T("Runtime.Session.RemovalIncomplete", detail, removalError);
            AntiFadeError = detail;
            _retryAntiFade = true; _nextAntiFadeRetry = DateTime.UtcNow + FeatureRetryInterval;
            ReportAntiFade(L10n.T("Runtime.Session.AntiFadeUnavailable", detail));
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
                error = ErrorOr(_fov.LastError, L10n.T("Runtime.Session.FovNotRestored"));
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
                error = ErrorOr(_antiFade.LastError, L10n.T("Runtime.Session.AntiFadeNotRestored"));
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
                AntiFadeError = error; ReportAntiFade(L10n.T("Runtime.Session.AntiFadeRestoreIncomplete", error));
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
                { restored = false; AntiFadeError = ex.Message; LogAntiFade(L10n.T("Runtime.Session.AntiFadeCleanupFailed", ex.Message)); }
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
                FovError = error; ReportFov(L10n.T("Runtime.Session.FovRestoreIncomplete", error));
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
                { restored = false; FovError = ex.Message; LogFov(L10n.T("Runtime.Session.FovCleanupFailed", ex.Message)); }
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
                SafeLog(L10n.T("Runtime.Session.CapRestoreRetry"));
            }
            catch (Exception ex) { SafeLog(L10n.T("Runtime.Session.CapRestoreFailed", ex.Message)); }
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

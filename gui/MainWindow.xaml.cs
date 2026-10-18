using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StarRailUnlocker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace StarRailUnlocker
{
    /// <summary>
    /// Main window. Unlocking remains in <see cref="UnlockSession"/>; this class owns UI
    /// state, settings, process selection, and user-facing diagnostics.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const int FpsMin = 10;
        private const int FpsMax = 480;
        private static readonly int[] FpsPresets = { 60, 120, 144, 165, 240, 420 };

        private readonly UnlockConfig _config;
        private UnlockSession _session;
        private bool _syncingFps, _syncingFov;
        private bool _initializing;
        private TrayIcon _tray;
        private WindowLayout _windowLayout;
        private DispatcherTimer _saveTimer, _refreshTimer;
        private bool _started, _closed, _hidingToTray, _inTray;
        private bool _closePending, _closeCompleted, _switchingSession, _stoppingSession;
        private bool _startupComplete, _setupInProgress, _dialogOpen, _pickingGame;
        private IReadOnlyList<string> _detectedGamePaths = Array.Empty<string>();
        private XamlRoot _dialogRoot;
        private Action<SessionState, string> _sessionHandler;
        private DispatcherTimer _exitTimer;
        private System.Threading.Tasks.Task _sessionDrain = System.Threading.Tasks.Task.CompletedTask;

        private sealed class RunningGameCandidate
        {
            public uint Pid;
            public string Path;
        }

        public MainWindow()
        {
            // XAML wires ValueChanged handlers while InitializeComponent runs.  Keep the
            // loaded settings immutable until all controls have their supported ranges and
            // values; otherwise a control's default value can overwrite TargetFramerate.
            _initializing = true;
            _updateChecker = new UpdateChecker(_updateHttp);
            _config = UnlockConfig.Load();
            _config.Normalize();
            L10n.SetLanguage(_config.Language);
            App.WriteLog("Startup", L10n.T("App.Starting", AppVersion));

            try
            {
                InitializeComponent();
                CreateFpsPresets();
                ApplyLocalization();
                ConfigureNumericControls();

                if (!string.IsNullOrWhiteSpace(_config.ProcessPath) &&
                    !File.Exists(_config.ProcessPath))
                {
                    Log(L10n.T("Ui.LogInvalidConfiguredPath", _config.ProcessPath));
                    _config.ProcessPath = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(_config.ProcessPath))
                {
                    Log(L10n.T("Ui.LogConfiguredPath", _config.ProcessPath));
                }

                RefreshGameClients();
                ApplyFps(_config.TargetFramerate);
                AutoStartToggle.IsOn = _config.AutoStart;
                FovBox.Value = _config.FovDelta;
                FovSlider.Value = _config.FovDelta;
                AntiFadeToggle.IsOn = _config.DisableCameraFade;
                MobileUiToggle.IsOn = _config.UseMobileUI;
                GraphicsSourceBox.SelectedIndex = (int)_config.GraphicsSource;
                GraphicsSourceBox.IsEnabled = _config.UseMobileUI;
                SafeAreaModeBox.SelectedIndex = (int)_config.SafeAreaMode;
                SafeAreaHorizontalBox.Value = _config.SafeAreaHorizontalPercent;
                StartMinimizedToggle.IsOn = _config.StartMinimized;
                ExitWithGameToggle.IsOn = _config.ExitWithGame;
                CheckForUpdatesToggle.IsOn = _config.CheckForUpdates;
                UpdateSourceBox.SelectedIndex = (int)_config.UpdateSource;

                Log(L10n.T("Ui.LogConfigPath", UnlockConfig.DefaultPath));
                ConfigureWindow();
                RefreshPresentation();
            }
            catch (Exception ex)
            {
                App.WriteLog("MainWindow initialization", ex);
                throw;
            }
            finally
            {
                _initializing = false;
            }

            AppWindow.Changed += (sender, args) =>
            {
                if (!_hidingToTray && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                    presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                    DispatcherQueue.TryEnqueue(() => HideToTray());
            };
            AppWindow.Closing += (_, args) =>
            {
                if (_closeCompleted) return;
                args.Cancel = true;
                BeginCloseAsync();
            };
            Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                    QueueUpdatePrompt();
            };
            Closed += (_, __) =>
            {
                _closed = true;
                _saveTimer?.Stop(); _refreshTimer?.Stop(); _exitTimer?.Stop();
                DisposeUpdateCheck();
                SyncConfig(); _config.Save();
                _tray?.Dispose();
                _windowLayout?.Dispose();
            };
        }

        internal void OnOpened()
        {
            if (_started || _closed || _closePending) return;
            _started = true;
            _setupInProgress = !IsValidGamePath(_config.ProcessPath);
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _tray = new TrayIcon(hwnd, () =>
                {
                    if (_closed || _closePending) return;
                    _inTray = false;
                    Activate();
                    QueueUpdatePrompt();
                }, RequestStop, BeginCloseAsync);
                if (!_tray.Add()) Log(L10n.T("Ui.LogTrayInitializationFailed"));
            }
            catch (Exception ex) { App.WriteLog("Tray initialization", ex); }
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _saveTimer.Tick += (_, __) => { if (!_closed) { SyncConfig(); _config.Save(); } };
            _saveTimer.Start();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += (_, __) => RefreshPresentation();
            _refreshTimer.Start();
            if (_config.CheckForUpdates) _ = CheckUpdatesAsync();
            RefreshPresentation();
            var rootElement = (FrameworkElement)Content;
            if (rootElement.IsLoaded)
            {
                _dialogRoot = rootElement.XamlRoot;
                DispatcherQueue.TryEnqueue(() => _ = StartupDialogsAsync());
            }
            else rootElement.Loaded += OnStartupContentLoaded;
        }

        private void OnStartupContentLoaded(object sender, RoutedEventArgs args)
        {
            var rootElement = (FrameworkElement)sender;
            rootElement.Loaded -= OnStartupContentLoaded;
            if (_closed || _closePending) return;
            _dialogRoot = rootElement.XamlRoot;
            DispatcherQueue.TryEnqueue(() => _ = StartupDialogsAsync());
        }

        private void HideToTray(bool force = false)
        {
            if (_closed || _closePending || _hidingToTray || _inTray) return;
            if (_setupInProgress)
            {
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter setupPresenter &&
                    setupPresenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    setupPresenter.Restore();
                    Activate();
                }
                return;
            }
            if (_tray == null) return;
            if (!force && (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter ||
                presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)) return;
            try
            {
                _hidingToTray = true;
                _tray.HideWindow();
                _inTray = true;
                Log(L10n.T("Ui.LogHiddenToTray"));
            }
            catch (Exception ex) { Log(ex.Message); App.WriteLog("Hide to tray", ex); }
            finally { _hidingToTray = false; }
        }

        /// <summary>Release label shown in the title bar and the persistent log.</summary>
        internal static string AppVersion
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string value = null;
                object[] attributes = assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false);
                if (attributes.Length > 0)
                    value = ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                if (string.IsNullOrWhiteSpace(value))
                    value = assembly.GetName().Version?.ToString() ?? L10n.T("Ui.UnknownVersion");
                int build = value.IndexOf('+');
                return build > 0 ? value[..build] : value;
            }
        }

        private void ConfigureNumericControls()
        {
            // These setters can raise ValueChanged during XAML/control initialization.  The
            // _initializing guard in each handler prevents them from changing loaded values.
            FpsSlider.Minimum = FpsMin;
            FpsSlider.Maximum = FpsMax;
            FpsSlider.StepFrequency = 1;

            FpsBox.Minimum = FpsMin;
            FpsBox.Maximum = FpsMax;
            FpsBox.SmallChange = 1;

            FovSlider.Minimum = -60;
            FovSlider.Maximum = 60;
            FovSlider.StepFrequency = 0.01;
            FovBox.Minimum = -60;
            FovBox.Maximum = 60;
            FovBox.SmallChange = 0.01;

            SafeAreaHorizontalBox.Minimum = 0;
            SafeAreaHorizontalBox.Maximum = 15;
            SafeAreaHorizontalBox.SmallChange = 0.5;
            SafeAreaHorizontalBox.LargeChange = 1;
        }

        private void OnAntiFadeToggled(object sender, RoutedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            _config.DisableCameraFade = AntiFadeToggle.IsOn;
            _session?.ApplyAntiFade();
            RefreshPresentation();
        }

        private void OnMobileUiToggled(object sender, RoutedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            _config.UseMobileUI = MobileUiToggle.IsOn;
            RefreshPresentation();
        }

        private void OnGraphicsSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            if (GraphicsSourceBox.SelectedIndex is >= 0 and <= 2)
                _config.GraphicsSource = (MobileGraphicsSource)GraphicsSourceBox.SelectedIndex;
        }

        private void OnSafeAreaModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || _closed || _closePending || !SafeAreaModeBox.IsEnabled) return;
            if (SafeAreaModeBox.SelectedIndex is >= 0 and <= 1)
                _config.SafeAreaMode = (MobileSafeAreaMode)SafeAreaModeBox.SelectedIndex;
            RefreshPresentation();
        }

        private void OnSafeAreaHorizontalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_initializing || _closed || _closePending || !sender.IsEnabled || !double.IsFinite(args.NewValue)) return;
            _config.SafeAreaHorizontalPercent = Math.Clamp(args.NewValue, 0, 15);
        }

        private void OnExitWithGameToggled(object sender, RoutedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            _config.ExitWithGame = ExitWithGameToggle.IsOn;
            CancelGameExitClose();
            if (_config.ExitWithGame && _session?.State == SessionState.GameExited)
                ScheduleGameExitClose(_session);
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            RefreshPresentation();
        }

        private void CancelGameExitClose()
        {
            _exitTimer?.Stop();
            _exitTimer = null;
        }

        private void ScheduleGameExitClose(UnlockSession source)
        {
            CancelGameExitClose();
            if (!_config.ExitWithGame || _closed || _closePending || _switchingSession || _downloadingUpdate ||
                !ReferenceEquals(_session, source)) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _exitTimer = timer;
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                if (!ReferenceEquals(_exitTimer, timer)) return;
                _exitTimer = null;
                if (_config.ExitWithGame && !_closed && !_closePending && !_switchingSession &&
                    ReferenceEquals(_session, source) && source.State == SessionState.GameExited)
                    BeginCloseAsync();
            };
            timer.Start();
        }

        // ------------------------------------------------------------ game discovery
        /// <summary>Enumerates actual executable paths without inferring a channel from directory names.</summary>
        private List<RunningGameCandidate> FindRunningGames()
        {
            var candidates = new List<RunningGameCandidate>();
            bool foundProcess = false;

            try
            {
                foreach (uint pid in TargetProcess.FindProcessIds("StarRail.exe"))
                {
                    foundProcess = true;
                    try
                    {
                        var (_, _, modulePath) = TargetProcess.FindMainModule(pid);
                        if (string.IsNullOrWhiteSpace(modulePath))
                        {
                            Log(L10n.T("Ui.LogModulePathUnavailable", pid));
                            continue;
                        }

                        string fullPath;
                        try
                        {
                            fullPath = Path.GetFullPath(modulePath);
                        }
                        catch (Exception ex)
                        {
                            Log(L10n.T("Ui.LogModulePathInvalid", pid, modulePath));
                            App.WriteLog("FindMainModule / invalid path", ex);
                            continue;
                        }

                        if (!File.Exists(fullPath))
                        {
                            Log(L10n.T("Ui.LogModulePathInaccessible", pid, fullPath));
                            continue;
                        }

                        candidates.Add(new RunningGameCandidate { Pid = pid, Path = fullPath });
                        Log(L10n.T("Ui.LogFoundGame", pid, fullPath));
                    }
                    catch (Exception ex)
                    {
                        Log(L10n.T("Ui.LogModuleEnumerationFailed", pid));
                        App.WriteLog("FindMainModule / enumeration", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(L10n.T("Ui.LogProcessEnumerationFailed"));
                App.WriteLog("FindProcessIds", ex);
                return candidates;
            }

            if (!foundProcess)
            {
                Log(L10n.T("Ui.LogNoRunningGame"));
                return candidates;
            }

            if (candidates.Count == 0)
            {
                Log(L10n.T("Ui.LogNoAccessibleGame"));
                return candidates;
            }
            candidates.Sort((a, b) => a.Pid.CompareTo(b.Pid));
            return candidates;
        }

        private bool SetDetectedGame(uint pid, string path, string messageKey)
        {
            if (!TrySetGamePath(path)) return false;
            Log(L10n.T(messageKey, pid, path));
            return true;
        }

        // ------------------------------------------------------------------ logging
        // ------------------------------------------------------------------ fps glue
        private void ApplyFps(int value)
        {
            value = Math.Clamp(value, FpsMin, FpsMax);
            _syncingFps = true;
            try
            {
                FpsSlider.Value = value;
                FpsBox.Value = value;
            }
            finally
            {
                _syncingFps = false;
            }

            _config.TargetFramerate = value;
            RefreshPresetSelection();
        }

        private void OnFpsSliderChanged(object sender,
            Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_initializing || _syncingFps || !double.IsFinite(e.NewValue)) return;
            ApplyFps((int)Math.Round(Math.Clamp(e.NewValue, FpsMin, FpsMax)));
        }

        private void OnFpsBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_initializing || _syncingFps || !double.IsFinite(args.NewValue)) return;
            ApplyFps((int)Math.Round(Math.Clamp(args.NewValue, FpsMin, FpsMax)));
        }

        /// <summary>
        /// FOV edits request an update from the live session.  The visible state remains
        /// pending verification until the session exposes a verified runtime result.
        /// </summary>
        private void OnFovSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_initializing || _syncingFov || !double.IsFinite(e.NewValue)) return;
            ApplyFovValue(e.NewValue);
        }

        private void OnFovBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_initializing || _syncingFov || !double.IsFinite(args.NewValue)) return;
            ApplyFovValue(args.NewValue);
        }

        private void ApplyFovValue(double value)
        {
            value = Math.Round(Math.Clamp(value, -60.0, 60.0), 2);
            bool changed = _config.FovDelta != value;
            _syncingFov = true;
            try { FovSlider.Value = FovBox.Value = value; }
            finally { _syncingFov = false; }
            _config.FovDelta = value;
            if (changed && _session != null)
            {
                Log(L10n.T("Ui.LogFovChanged", value));
                _session.ApplyFov();
            }
            RefreshPresentation();
        }

        private void OnResetFovClick(object sender, RoutedEventArgs e) => ApplyFovValue(0);

        private void OnFpsPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button && button.Tag is int value)
                ApplyFps(value);
        }

        // ------------------------------------------------------------------ buttons
        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            if (CanChangeGameClient) await ChooseGameAsync();
        }

        private static bool IsValidGamePath(string path) =>
            File.Exists(path) && string.Equals(Path.GetFileName(path), "StarRail.exe", StringComparison.OrdinalIgnoreCase);

        private async System.Threading.Tasks.Task<bool> ChooseGameAsync(bool duringSetup = false)
        {
            if (_closed || _closePending || _pickingGame || _scanningGames || _switchingSession ||
                _session?.IsRunning == true || _downloadingUpdate ||
                ((_setupInProgress || _dialogOpen) && !duringSetup)) return false;
            _pickingGame = true;
            RefreshPresentation();
            try
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (_closed || _closePending || file == null) return false;
                if (!IsValidGamePath(file.Path))
                {
                    Log(L10n.T("Ui.LogChooseGameExecutable"));
                    return false;
                }
                return TrySetGamePath(file.Path);
            }
            catch (Exception ex)
            {
                if (!_closed && !_closePending) Log(L10n.T("Ui.LogGamePickerFailed", ex.Message));
                App.WriteLog("Game picker", ex);
                return false;
            }
            finally
            {
                _pickingGame = false;
                if (!_closed && !_closePending) RefreshPresentation();
                QueueUpdatePrompt();
            }
        }

        private async System.Threading.Tasks.Task StartupDialogsAsync()
        {
            if (_closed || _closePending || _startupComplete) return;
            try
            {
                if (!IsValidGamePath(_config.ProcessPath))
                {
                    await ScanGameClientsAsync(duringSetup: true);
                    if (_closed || _closePending) return;
                    if (_detectedGamePaths.Count == 1 && !IsValidGamePath(_config.ProcessPath))
                    {
                        string detected = _detectedGamePaths[0];
                        if (TrySetGamePath(detected))
                            Log(L10n.T("Ui.LogInstalledGame", detected));
                    }
                }
                if (!IsValidGamePath(_config.ProcessPath)) await ShowSetupAsync();
                if (_closed || _closePending) return;
                _setupInProgress = false;
                if (_config.AutoStart)
                {
                    if (IsValidGamePath(_config.ProcessPath)) LaunchSession(0, launchIfMissing: true);
                    else Log(L10n.T("Ui.LogAutoStartNeedsPath"));
                }
                if (_config.StartMinimized) HideToTray(force: true);
            }
            catch (Exception ex) { App.WriteLog("Startup dialogs", ex); }
            finally
            {
                _setupInProgress = false;
                _startupComplete = true;
                RefreshPresentation();
                QueueUpdatePrompt();
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            SyncConfig();
            if (_config.Save())
            {
                Log(L10n.T("Ui.LogConfigSaved", UnlockConfig.DefaultPath));
            }
            else
            {
                Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            }
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeGameClient) return;
            SyncConfig();
            if (!IsValidGamePath(_config.ProcessPath))
            {
                Log(L10n.T("Ui.LogInvalidGamePath"));
                if (!await ChooseGameAsync()) return;
            }
            if (_closed || _closePending) return;
            Log(L10n.T("Ui.LogStartingGame"));
            LaunchSession(0, launchIfMissing: true);
        }

        private async void OnAttachClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeGameClient) return;
            SyncConfig();
            var games = FindRunningGames();
            if (games.Count == 0)
            {
                _localErrorKey = "Ui.NoAttachableGame";
                RefreshPresentation();
                return;
            }
            RunningGameCandidate selected = games.Count == 1 ? games[0] : await ChooseRunningGameAsync(games);
            if (selected == null || _closed || _closePending) return;
            if (!SetDetectedGame(selected.Pid, selected.Path, "Ui.LogAttachGame")) return;
            LaunchSession(selected.Pid);
        }

        private async System.Threading.Tasks.Task<RunningGameCandidate> ChooseRunningGameAsync(
            List<RunningGameCandidate> games)
        {
            if (_dialogRoot == null) return null;
            _dialogOpen = true;
            try
            {
                var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
                foreach (var game in games)
                {
                    var text = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
                    text.Children.Add(new TextBlock { Text = L10n.T("Ui.RunningGame", game.Pid), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    text.Children.Add(new TextBlock { Text = game.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    list.Items.Add(text);
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = _dialogRoot, Title = L10n.T("Ui.ChooseRunningGameTitle"), Content = list,
                    PrimaryButtonText = L10n.T("Ui.Attach"), CloseButtonText = L10n.T("Ui.Cancel"), IsPrimaryButtonEnabled = false
                };
                list.SelectionChanged += (_, __) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary && list.SelectedIndex >= 0
                    ? games[list.SelectedIndex] : null;
            }
            catch (Exception ex) { Log(L10n.T("Ui.LogProcessPickerFailed", ex.Message)); return null; }
            finally { _dialogOpen = false; QueueUpdatePrompt(); }
        }

        private void OnStopClick(object sender, RoutedEventArgs e) => RequestStop();

        private void RequestStop()
        {
            if (_closed || _closePending || _session?.IsRunning != true) return;
            _stoppingSession = true;
            Log(L10n.T("Ui.LogStopping"));
            _session?.Stop();
            RefreshPresentation();
        }

        // ------------------------------------------------------------------ session
        private async void BeginCloseAsync()
        {
            if (_closed || _closePending) return;
            _closePending = true;
            _updateCancellation.Cancel();
            RefreshPresentation();
            _saveTimer?.Stop(); _refreshTimer?.Stop(); CancelGameExitClose();
            SyncConfig(); _config.Save();
            var session = _session;
            if (session != null && _sessionHandler != null) session.Changed -= _sessionHandler;
            try
            {
                await _sessionDrain.ConfigureAwait(false);
                if (session != null) await System.Threading.Tasks.Task.Run(session.Dispose).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.WriteLog("Close teardown", ex);
                DispatcherQueue.TryEnqueue(() =>
                {
                    AbortPreparedUpdate();
                    _closePending = false;
                    _updateCancellation.Dispose();
                    _updateCancellation = new System.Threading.CancellationTokenSource();
                    if (session != null && _sessionHandler != null) session.Changed += _sessionHandler;
                    _updateStatusKey = "Updates.RestoreFailed";
                    _saveTimer?.Start();
                    _refreshTimer?.Start();
                    RefreshPresentation();
                });
                return;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                _session = null;
                CommitPreparedUpdate();
                _closeCompleted = true;
                Close();
            });
        }

        private async void LaunchSession(uint pid, bool launchIfMissing = false)
        {
            if (_switchingSession || _closePending || _closed || _scanningGames || _pickingGame) return;
            CancelGameExitClose();
            _switchingSession = true;
            _stoppingSession = false;
            _localErrorKey = null;
            RefreshPresentation();
            var old = _session;
            if (old != null && _sessionHandler != null) old.Changed -= _sessionHandler;
            _session = null;
            _sessionDrain = old == null ? System.Threading.Tasks.Task.CompletedTask : System.Threading.Tasks.Task.Run(old.Dispose);
            try { await _sessionDrain.ConfigureAwait(false); }
            catch (Exception ex) { App.WriteLog("Session replacement", ex); }
            DispatcherQueue.TryEnqueue(() =>
            {
                _switchingSession = false;
                if (_closePending || _closed) return;
                var session = new UnlockSession(_config);
                _session = session;
                _sessionHandler = (state, message) => OnSessionChanged(session, state, message);
                session.Changed += _sessionHandler;
                session.Start(pid, launchIfMissing);
            });
        }

        private void OnSessionChanged(UnlockSession source, SessionState state, string message)
        {
            string line = message == null ? null : L10n.T("Ui.LogSession", SessionStateText(state), message);
            if (line != null) App.WriteLog("Session", line);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || _closePending || !ReferenceEquals(_session, source)) return;
                if (state == SessionState.GameExited)
                    ScheduleGameExitClose(source);
                // A null message refreshes feature snapshots without repeating diagnostics.
                if (line != null) Log(line, persist: false);
                RefreshPresentation();
            });
        }

        private void SyncConfig()
        {
            if (double.IsFinite(FpsBox.Value))
                _config.TargetFramerate = (int)Math.Round(Math.Clamp(FpsBox.Value, FpsMin, FpsMax));
            _config.AutoStart = AutoStartToggle.IsOn;
            _config.StartMinimized = StartMinimizedToggle.IsOn;
            _config.ExitWithGame = ExitWithGameToggle.IsOn;
            _config.CheckForUpdates = CheckForUpdatesToggle.IsOn;
            if (double.IsFinite(FovBox.Value))
                _config.FovDelta = Math.Clamp(FovBox.Value, -60.0, 60.0);
            _config.DisableCameraFade = AntiFadeToggle.IsOn;
            _config.UseMobileUI = MobileUiToggle.IsOn;
            if (GraphicsSourceBox.SelectedIndex is >= 0 and <= 2)
                _config.GraphicsSource = (MobileGraphicsSource)GraphicsSourceBox.SelectedIndex;
            if (SafeAreaModeBox.SelectedIndex is >= 0 and <= 1)
                _config.SafeAreaMode = (MobileSafeAreaMode)SafeAreaModeBox.SelectedIndex;
            if (double.IsFinite(SafeAreaHorizontalBox.Value))
                _config.SafeAreaHorizontalPercent = SafeAreaHorizontalBox.Value;
            _config.Normalize();
        }
    }
}

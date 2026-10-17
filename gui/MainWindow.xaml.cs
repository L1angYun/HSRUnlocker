using System;
using System.Collections.Generic;
using System.IO;
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
            _config = UnlockConfig.Load();
            _config.Normalize();

            try
            {
                InitializeComponent();
                Localise();
                ConfigureNumericControls();

                if (!string.IsNullOrWhiteSpace(_config.ProcessPath) &&
                    !File.Exists(_config.ProcessPath))
                {
                    Log("配置中的游戏路径已失效，改为自动检测: " + _config.ProcessPath);
                    _config.ProcessPath = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(_config.ProcessPath))
                {
                    Log("配置中的游戏路径: " + _config.ProcessPath);
                }

                PathBox.Text = _config.ProcessPath;
                ApplyFps(_config.TargetFramerate);
                AutoStartToggle.IsOn = _config.AutoStart;
                FovBox.Value = _config.FovDelta;
                FovSlider.Value = _config.FovDelta;
                AntiFadeToggle.IsOn = _config.DisableCameraFade;
                MobileUiToggle.IsOn = _config.UseMobileUI;
                GraphicsSourceBox.SelectedIndex = (int)_config.GraphicsSource;
                GraphicsSourceBox.IsEnabled = _config.UseMobileUI;
                StartMinimizedToggle.IsOn = _config.StartMinimized;

                Log("配置文件: " + UnlockConfig.DefaultPath);
                if (string.IsNullOrWhiteSpace(_config.ProcessPath))
                    TryAutoDetectGame();
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
            Closed += (_, __) =>
            {
                _closed = true;
                _saveTimer?.Stop(); _refreshTimer?.Stop(); _exitTimer?.Stop();
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
                }, RequestStop, BeginCloseAsync);
                if (!_tray.Add()) Log("托盘图标初始化失败，窗口保持可见");
            }
            catch (Exception ex) { App.WriteLog("Tray initialization", ex); }
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _saveTimer.Tick += (_, __) => { if (!_closed) { SyncConfig(); _config.Save(); } };
            _saveTimer.Start();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += (_, __) => RefreshPresentation();
            _refreshTimer.Start();
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
                Log("窗口已最小化到托盘；双击图标可恢复");
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
                    value = assembly.GetName().Version?.ToString() ?? "unknown";
                int build = value.IndexOf('+');
                return build > 0 ? value[..build] : value;
            }
        }

        /// <summary>Version, choices, and tooltips supplement the native XAML layout.</summary>
        private void Localise()
        {
            Title = "HSRUnlocker " + AppVersion;
            VersionText.Text = "Honkai: Star Rail · " + AppVersion;
            PathBox.PlaceholderText = "选择 StarRail.exe";
            ToolTipService.SetToolTip(AntiFadeToggle, "仅针对相机靠近或俯仰角引起的角色淡出；普通生成与剧情淡入淡出保持正常。可在游戏运行时开关，关闭后恢复。");
            ToolTipService.SetToolTip(AutoStartToggle, "下次打开解锁器时自动启动所选客户端并应用设置");
            ToolTipService.SetToolTip(StartMinimizedToggle, "下次打开解锁器时隐藏到托盘；双击托盘图标恢复");
            ToolTipService.SetToolTip(MobileUiToggle, "仅在下次由工具启动游戏时应用移动 UI；附加到已运行客户端时不会改变界面");
            ToolTipService.SetToolTip(GraphicsSourceBox, "选择移动 UI 启动时沿用的画质设置。默认根据客户端信息自动识别；国服与国际服的设置保存位置不同。");
            ToolTipService.SetToolTip(FovSlider, "视野增量，单位为度，范围 −60 至 60");
            ToolTipService.SetToolTip(FovBox, "FOV 增量，单位为度；0 为游戏原始视野，下一次镜头更新时应用");
            ToolTipService.SetToolTip(FpsSlider, "目标帧率 10 至 480；游戏重设上限时自动恢复");
            ToolTipService.SetToolTip(FpsBox, "目标帧率，单位为 FPS");
            GraphicsAutoItem.Content = "自动（当前客户端）";
            GraphicsChinaItem.Content = "沿用国服画质";
            GraphicsGlobalItem.Content = "沿用国际服画质";
            ToolTipService.SetToolTip(StartButton, "启动所选客户端并应用设置；该客户端已运行时直接附加");
            ToolTipService.SetToolTip(AttachButton, "附加正在运行的游戏并应用设置");
            for (int i = 0; i < FpsPresets.Length; i++)
            {
                int value = FpsPresets[i];
                var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Content = value.ToString(), Tag = value, MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 6, 4, 6)
                };
                Grid.SetColumn(button, i);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, value + " FPS");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "FpsPreset" + value);
                ToolTipService.SetToolTip(button, value + " FPS");
                button.Click += OnFpsPresetClick;
                PresetPanel.Children.Add(button);
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
            GraphicsSourceBox.IsEnabled = MobileUiToggle.IsOn;
        }

        private void OnGraphicsSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            if (GraphicsSourceBox.SelectedIndex is >= 0 and <= 2)
                _config.GraphicsSource = (MobileGraphicsSource)GraphicsSourceBox.SelectedIndex;
        }

        // ------------------------------------------------------------ game discovery
        private void TryAutoDetectGame()
        {
            var games = FindRunningGames();
            if (games.Count == 1)
            {
                SetDetectedGame(games[0].Pid, games[0].Path, "自动检测到正在运行的游戏");
                return;
            }
            if (games.Count > 1)
            {
                Log("检测到多个游戏进程，可在附加时选择目标");
                return;
            }
            Log("未自动检测到游戏，请点「浏览」选择 StarRail.exe");
        }

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
                            Log($"pid {pid}: 无法枚举主模块路径（进程可能已退出或权限不足）");
                            continue;
                        }

                        string fullPath;
                        try
                        {
                            fullPath = Path.GetFullPath(modulePath);
                        }
                        catch (Exception ex)
                        {
                            Log($"pid {pid}: 主模块路径无效: {modulePath}");
                            App.WriteLog("FindMainModule / invalid path", ex);
                            continue;
                        }

                        if (!File.Exists(fullPath))
                        {
                            Log($"pid {pid}: 主模块路径不可访问或文件已消失: {fullPath}");
                            continue;
                        }

                        candidates.Add(new RunningGameCandidate { Pid = pid, Path = fullPath });
                        Log($"发现 StarRail.exe pid {pid}: {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        Log($"pid {pid}: 枚举主模块时出错");
                        App.WriteLog("FindMainModule / enumeration", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("枚举 StarRail.exe 进程失败，无法安全附加");
                App.WriteLog("FindProcessIds", ex);
                return candidates;
            }

            if (!foundProcess)
            {
                Log("未找到正在运行的 StarRail.exe");
                return candidates;
            }

            if (candidates.Count == 0)
            {
                Log("找到 StarRail.exe，但无法取得任何主模块路径；请确认管理员权限后重试");
                return candidates;
            }
            candidates.Sort((a, b) => a.Pid.CompareTo(b.Pid));
            return candidates;
        }

        private void SetDetectedGame(uint pid, string path, string prefix)
        {
            PathBox.Text = path;
            _config.ProcessPath = path;
            Log($"{prefix}: pid {pid}, {path}");
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
                Log($"FOV 增量设为 {value:0.##}");
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
        private async void OnBrowseClick(object sender, RoutedEventArgs e) => await ChooseGameAsync();

        private static bool IsValidGamePath(string path) =>
            File.Exists(path) && string.Equals(Path.GetFileName(path), "StarRail.exe", StringComparison.OrdinalIgnoreCase);

        private async System.Threading.Tasks.Task<bool> ChooseGameAsync()
        {
            if (_closed || _closePending || _pickingGame) return false;
            _pickingGame = true;
            try
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (_closed || _closePending || file == null) return false;
                if (!IsValidGamePath(file.Path))
                {
                    Log("请选择客户端目录中的 StarRail.exe");
                    return false;
                }
                PathBox.Text = file.Path;
                _config.ProcessPath = file.Path;
                _localError = null;
                _config.Save();
                Log("游戏路径已设为: " + file.Path);
                return true;
            }
            catch (Exception ex)
            {
                if (!_closed && !_closePending) Log("选择游戏失败: " + ex.Message);
                App.WriteLog("Game picker", ex);
                return false;
            }
            finally
            {
                _pickingGame = false;
            }
        }

        private async System.Threading.Tasks.Task StartupDialogsAsync()
        {
            if (_closed || _closePending || _startupComplete) return;
            try
            {
                if (!IsValidGamePath(_config.ProcessPath)) await ShowSetupAsync();
                if (_closed || _closePending) return;
                _setupInProgress = false;
                if (_config.AutoStart)
                {
                    if (IsValidGamePath(_config.ProcessPath)) LaunchSession(0, launchIfMissing: true);
                    else Log("自启动已开启，但尚未选择有效游戏路径；请先选择 StarRail.exe");
                }
                if (_config.StartMinimized) HideToTray(force: true);
                _startupComplete = true;
            }
            catch (Exception ex) { App.WriteLog("Startup dialogs", ex); }
            finally { _setupInProgress = false; }
        }

        private async System.Threading.Tasks.Task ShowSetupAsync()
        {
            if (_closed || _closePending || _dialogOpen || _dialogRoot == null) return;
            _setupInProgress = true;
            _dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = _dialogRoot,
                    Title = "设置游戏路径",
                    Content = "请选择客户端目录中的 StarRail.exe。",
                    PrimaryButtonText = "选择 StarRail.exe",
                    CloseButtonText = "稍后设置",
                    DefaultButton = ContentDialogButton.Primary,
                };
                var result = await dialog.ShowAsync();
                if (_closed || _closePending) return;
                if (result == ContentDialogResult.Primary)
                    await ChooseGameAsync();
            }
            catch (Exception ex) { App.WriteLog("First-run setup", ex); }
            finally { _dialogOpen = false; _setupInProgress = false; }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            SyncConfig();
            if (_config.Save())
            {
                Log("配置已保存: " + UnlockConfig.DefaultPath);
            }
            else
            {
                Log("配置保存失败，请查看工具日志: " + UnlockConfig.LogPath);
            }
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (_closed || _closePending) return;
            SyncConfig();
            if (!IsValidGamePath(_config.ProcessPath))
            {
                Log("当前游戏路径无效，请选择 StarRail.exe");
                if (!await ChooseGameAsync()) return;
            }
            if (_closed || _closePending) return;
            Log("开始：查找或启动游戏…");
            LaunchSession(0, launchIfMissing: true);
        }

        private async void OnAttachClick(object sender, RoutedEventArgs e)
        {
            if (_closed || _closePending || _switchingSession || _dialogOpen) return;
            SyncConfig();
            var games = FindRunningGames();
            if (games.Count == 0)
            {
                _localError = "未找到可附加的 StarRail.exe，请确认游戏已启动且权限可访问";
                RefreshPresentation();
                return;
            }
            RunningGameCandidate selected = games.Count == 1 ? games[0] : await ChooseRunningGameAsync(games);
            if (selected == null || _closed || _closePending) return;
            SetDetectedGame(selected.Pid, selected.Path, "附加到");
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
                    text.Children.Add(new TextBlock { Text = $"StarRail.exe · PID {game.Pid}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    text.Children.Add(new TextBlock { Text = game.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    list.Items.Add(text);
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = _dialogRoot, Title = "选择正在运行的游戏", Content = list,
                    PrimaryButtonText = "附加", CloseButtonText = "取消", IsPrimaryButtonEnabled = false
                };
                list.SelectionChanged += (_, __) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary && list.SelectedIndex >= 0
                    ? games[list.SelectedIndex] : null;
            }
            catch (Exception ex) { Log("选择进程失败：" + ex.Message); return null; }
            finally { _dialogOpen = false; }
        }

        private void OnStopClick(object sender, RoutedEventArgs e) => RequestStop();

        private void RequestStop()
        {
            if (_closed || _closePending || _session?.IsRunning != true) return;
            _stoppingSession = true;
            Log("停止中（将还原帧率、FOV 与反虚化设置）…");
            _session?.Stop();
            RefreshPresentation();
        }

        // ------------------------------------------------------------------ session
        private async void BeginCloseAsync()
        {
            if (_closed || _closePending) return;
            _closePending = true;
            RefreshPresentation();
            _saveTimer?.Stop(); _refreshTimer?.Stop(); _exitTimer?.Stop();
            SyncConfig(); _config.Save();
            var session = _session;
            if (session != null && _sessionHandler != null) session.Changed -= _sessionHandler;
            _session = null;
            try
            {
                await _sessionDrain.ConfigureAwait(false);
                if (session != null) await System.Threading.Tasks.Task.Run(session.Dispose).ConfigureAwait(false);
            }
            catch (Exception ex) { App.WriteLog("Close teardown", ex); }
            DispatcherQueue.TryEnqueue(() => { _closeCompleted = true; Close(); });
        }

        private async void LaunchSession(uint pid, bool launchIfMissing = false)
        {
            if (_switchingSession || _closePending || _closed) return;
            _exitTimer?.Stop();
            _exitTimer = null;
            _switchingSession = true;
            _stoppingSession = false;
            _localError = null;
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
            if (message != null) App.WriteLog("Session", "[" + state + "] " + message);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || _closePending || !ReferenceEquals(_session, source)) return;
                if (state == SessionState.GameExited)
                {
                    _exitTimer?.Stop();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _exitTimer = timer;
                    timer.Tick += (_, __) =>
                    {
                        timer.Stop();
                        if (ReferenceEquals(_exitTimer, timer)) BeginCloseAsync();
                    };
                    timer.Start();
                    RefreshPresentation();
                    return;
                }
                // A null message refreshes feature snapshots without repeating diagnostics.
                if (message != null) Log("[" + state + "] " + message, persist: false);
                RefreshPresentation();
            });
        }

        private void SyncConfig()
        {
            _config.ProcessPath = PathBox.Text ?? string.Empty;
            if (double.IsFinite(FpsBox.Value))
                _config.TargetFramerate = (int)Math.Round(Math.Clamp(FpsBox.Value, FpsMin, FpsMax));
            _config.AutoStart = AutoStartToggle.IsOn;
            _config.StartMinimized = StartMinimizedToggle.IsOn;
            if (double.IsFinite(FovBox.Value))
                _config.FovDelta = Math.Clamp(FovBox.Value, -60.0, 60.0);
            _config.DisableCameraFade = AntiFadeToggle.IsOn;
            _config.UseMobileUI = MobileUiToggle.IsOn;
            if (GraphicsSourceBox.SelectedIndex is >= 0 and <= 2)
                _config.GraphicsSource = (MobileGraphicsSource)GraphicsSourceBox.SelectedIndex;
            _config.Normalize();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using StarRailUnlocker.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace StarRailUnlocker
{
    public sealed partial class MainWindow
    {
        private const int VisibleLogLimit = 300;
        private readonly Queue<string> _logLines = new();
        private string _lastLoggedLine, _localErrorKey;
        private bool _logDirty;
        private bool? _wideLayout;

        private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DisplaySettings == null || StartupSettings == null) return;
            bool wide = e.NewSize.Width >= 840;
            if (_wideLayout == wide) return;
            _wideLayout = wide;
            Grid.SetColumnSpan(DisplaySettings, wide ? 1 : 3);
            Grid.SetRow(StartupSettings, wide ? 0 : 1);
            Grid.SetColumn(StartupSettings, wide ? 2 : 0);
            Grid.SetColumnSpan(StartupSettings, wide ? 1 : 3);
            StartupSettings.Margin = wide ? new Thickness(0) : new Thickness(0, 28, 0, 0);
            SettingsDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ConfigureWindow()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _windowLayout = new WindowLayout(hwnd);
            double scale = WindowLayout.ScaleForWindow(hwnd);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = (int)Math.Min(980 * scale, area.Width - 48 * scale);
            int height = (int)Math.Min(760 * scale, area.Height - 64 * scale);
            AppWindow.ResizeClient(new SizeInt32(Math.Max(width, 320), Math.Max(height, 320)));
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - AppWindow.Size.Height) / 2)));
            string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }

        private void RefreshPresetSelection()
        {
            foreach (var child in PresetPanel.Children)
                if (child is ToggleButton button && button.Tag is int value)
                    button.IsChecked = value == _config.TargetFramerate;
        }

        private void RefreshPresentation()
        {
            if (_closed || StateTitle == null) return;
            var session = _session;
            bool running = session?.IsRunning == true;
            bool blocked = _closePending || _switchingSession || _setupInProgress;
            bool restoring = running && (_stoppingSession ||
                session.State is SessionState.Failed or SessionState.GameExited);
            if (!running) _stoppingSession = false;
            StartButton.IsEnabled = AttachButton.IsEnabled = !blocked && !running;
            StopButton.IsEnabled = !blocked && running && !restoring;
            BrowseButton.IsEnabled = !blocked && !running && !_pickingGame;
            SaveButton.IsEnabled = !blocked;
            FpsSlider.IsEnabled = FpsBox.IsEnabled = FovSlider.IsEnabled = FovBox.IsEnabled =
                ResetFovButton.IsEnabled = AntiFadeToggle.IsEnabled = !blocked && !restoring;
            foreach (var child in PresetPanel.Children)
                if (child is ToggleButton button) button.IsEnabled = FpsSlider.IsEnabled;
            bool preparing = running && session.State is SessionState.WaitingForGame or SessionState.Patching;
            MobileUiToggle.IsEnabled = AutoStartToggle.IsEnabled = StartMinimizedToggle.IsEnabled =
                !blocked && !preparing && !restoring;
            GraphicsSourceBox.IsEnabled = MobileUiToggle.IsEnabled && _config.UseMobileUI;
            ExitWithGameToggle.IsEnabled = !blocked;
            RefreshUpdatePresentation();

            if (_closePending || _switchingSession || restoring)
            {
                StateTitle.Text = L10n.T(_closePending ? "Ui.StateExiting" :
                    _switchingSession ? "Ui.StatePreparing" : "Ui.StateRestoring");
                StatusText.Text = restoring || _closePending
                    ? L10n.T("Ui.StatusRestoring")
                    : L10n.T("Ui.StatusEndingPreviousSession");
                StatusIcon.Glyph = "\uE895";
            }
            else
            {
                var state = session?.State ?? SessionState.Idle;
                StateTitle.Text = state switch
                {
                    SessionState.WaitingForGame => L10n.T("Ui.StateWaiting"),
                    SessionState.Patching => L10n.T("Ui.StateAttaching"),
                    SessionState.Active => L10n.T("Ui.StateActive"),
                    SessionState.Failed => L10n.T("Ui.StateFailed"),
                    SessionState.GameExited => L10n.T("Ui.StateGameExited"),
                    _ => L10n.T(_localErrorKey != null ? "Ui.StateGameNotFound" :
                        session == null ? "Ui.StateReady" : "Ui.StateStopped")
                };
                StatusText.Text = state switch
                {
                    SessionState.WaitingForGame => L10n.T("Ui.StatusWaiting"),
                    SessionState.Patching => L10n.T("Ui.StatusAttaching"),
                    SessionState.Active => L10n.T("Ui.StatusActive", session.AppliedValue, session.Pid),
                    SessionState.Failed => session.LastMessage,
                    SessionState.GameExited => L10n.T(_config.ExitWithGame
                        ? "Ui.StatusGameExited" : "Updates.GameExitedStayingOpen"),
                    _ => _localErrorKey != null ? L10n.T(_localErrorKey) :
                        session == null ? L10n.T("Ui.StatusNotAttached") :
                        session.OriginalValue > 0 ? L10n.T("Ui.StatusRestoredFps", session.OriginalValue) :
                        L10n.T("Ui.StatusRestored")
                };
                StatusIcon.Glyph = state switch
                {
                    SessionState.Active => "\uE73E",
                    SessionState.Failed => "\uE783",
                    SessionState.WaitingForGame or SessionState.Patching => "\uE895",
                    _ => _localErrorKey == null ? "\uE930" : "\uE783"
                };
            }

            FpsStateText.Text = session?.State == SessionState.Active && !restoring
                ? L10n.T("Ui.FpsApplied", session.AppliedValue, session.ReapplyCount) : L10n.T("Ui.NotEnabled");
            FovStateText.Text = FovStatusText();
            AntiFadeStateText.Text = AntiFadeStatusText();
        }

        private string FovStatusText()
        {
            if (_session?.State != SessionState.Active || _stoppingSession || _closePending)
                return _config.FovDelta == 0 ? L10n.T("Ui.OriginalFov") : L10n.T("Ui.FovPending", _config.FovDelta);
            if (!string.IsNullOrEmpty(_session.FovError)) return L10n.T("Ui.FeatureUnavailable", _session.FovError);
            if (_config.FovDelta == 0) return L10n.T(_session.FovInstalled ? "Ui.StateRestoring" : "Ui.OriginalFov");
            if (!_session.FovInstalled) return L10n.T("Ui.WaitingForCamera");
            var stats = _session.FovStatistics;
            return stats == null || stats.Calls == 0
                ? L10n.T("Ui.FovAppliedWaiting")
                : L10n.T("Ui.FovEffective", stats.Input, stats.Output);
        }

        private string AntiFadeStatusText()
        {
            if (_session?.State != SessionState.Active || _stoppingSession || _closePending)
                return L10n.T(_config.DisableCameraFade ? "Ui.Pending" : "Ui.NotEnabled");
            if (!string.IsNullOrEmpty(_session.AntiFadeError)) return L10n.T("Ui.FeatureUnavailable", _session.AntiFadeError);
            if (!_config.DisableCameraFade) return L10n.T(_session.AntiFadeInstalled ? "Ui.StateRestoring" : "Ui.NotEnabled");
            return L10n.T(_session.AntiFadeInstalled ? "Ui.Applied" : "Ui.WaitingToApply");
        }

        private void Log(string message, bool persist = true)
        {
            if (string.IsNullOrWhiteSpace(message) ||
                string.Equals(message, _lastLoggedLine, StringComparison.Ordinal)) return;
            _lastLoggedLine = message;
            if (persist) App.WriteLog("UI", message);
            _logLines.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            while (_logLines.Count > VisibleLogLimit) _logLines.Dequeue();
            _logDirty = true;
            RenderLog();
        }

        private void RenderLog(bool forceFollow = false)
        {
            if (LogText == null) return;
            CopyLogButton.IsEnabled = ClearLogButton.IsEnabled = _logLines.Count > 0;
            if (!_logDirty || (!LogExpander.IsExpanded && !forceFollow)) return;
            bool follow = forceFollow || LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 24;
            LogText.Text = string.Join(Environment.NewLine, _logLines);
            _logDirty = false;
            if (follow)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_closed) return;
                    LogScroll.UpdateLayout();
                    LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
                });
        }

        private void OnLogExpanding(Expander sender, ExpanderExpandingEventArgs args) => RenderLog(forceFollow: true);

        private void OnClearLogClick(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            _lastLoggedLine = null;
            LogText.Text = "";
            _logDirty = false;
            CopyLogButton.IsEnabled = ClearLogButton.IsEnabled = false;
        }

        private void OnCopyLogClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var data = new DataPackage();
                data.SetText(string.Join(Environment.NewLine, _logLines));
                Clipboard.SetContent(data);
            }
            catch (Exception ex) { Log(L10n.T("Ui.LogCopyFailed", ex.Message)); }
        }
    }
}

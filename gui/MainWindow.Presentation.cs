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
        private string _lastLoggedLine, _localError;
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
            bool blocked = _closePending || _switchingSession;
            bool restoring = running && (_stoppingSession || session.State == SessionState.Failed);
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

            if (_closePending || _switchingSession || restoring)
            {
                StateTitle.Text = _closePending ? "正在退出" : _switchingSession ? "正在准备会话" : "正在还原";
                StatusText.Text = restoring || _closePending
                    ? "正在还原帧率、FOV 与反虚化"
                    : "正在结束上一次会话";
                StatusIcon.Glyph = "\uE895";
            }
            else
            {
                var state = session?.State ?? SessionState.Idle;
                StateTitle.Text = state switch
                {
                    SessionState.WaitingForGame => "等待游戏",
                    SessionState.Patching => "正在附加",
                    SessionState.Active => "已解锁",
                    SessionState.Failed => "会话失败",
                    SessionState.GameExited => "游戏已退出",
                    _ => _localError != null ? "未找到游戏" : session == null ? "就绪" : "已停止"
                };
                StatusText.Text = state switch
                {
                    SessionState.WaitingForGame => "正在等待 StarRail.exe",
                    SessionState.Patching => "正在等待游戏模块并定位接口",
                    SessionState.Active => $"帧率上限 {session.AppliedValue} FPS · PID {session.Pid}",
                    SessionState.Failed => session.LastMessage,
                    SessionState.GameExited => "修改已释放，工具即将退出",
                    _ => _localError ?? (session == null ? "尚未附加游戏" :
                        session.OriginalValue > 0 ? $"已还原帧率上限为 {session.OriginalValue} FPS" : "已还原为游戏原始设置")
                };
                StatusIcon.Glyph = state switch
                {
                    SessionState.Active => "\uE73E",
                    SessionState.Failed => "\uE783",
                    SessionState.WaitingForGame or SessionState.Patching => "\uE895",
                    _ => _localError == null ? "\uE930" : "\uE783"
                };
            }

            FpsStateText.Text = session?.State == SessionState.Active && !restoring
                ? $"已应用 {session.AppliedValue} FPS · 自动恢复 {session.ReapplyCount} 次" : "未启用";
            FovStateText.Text = FovStatusText();
            AntiFadeStateText.Text = AntiFadeStatusText();
        }

        private string FovStatusText()
        {
            if (_session?.State != SessionState.Active || _stoppingSession || _closePending)
                return _config.FovDelta == 0 ? "原始视野" : $"待应用 · {_config.FovDelta:+0.##;-0.##;0}°";
            if (!string.IsNullOrEmpty(_session.FovError)) return "暂不可用 · " + _session.FovError;
            if (_config.FovDelta == 0) return _session.FovInstalled ? "正在还原" : "原始视野";
            if (!_session.FovInstalled) return "等待相机就绪";
            var stats = _session.FovStatistics;
            return stats == null || stats.Calls == 0
                ? "已应用，等待镜头更新"
                : $"已生效 · {stats.Input:0.##}° → {stats.Output:0.##}°";
        }

        private string AntiFadeStatusText()
        {
            if (_session?.State != SessionState.Active || _stoppingSession || _closePending)
                return _config.DisableCameraFade ? "待应用" : "未启用";
            if (!string.IsNullOrEmpty(_session.AntiFadeError)) return "暂不可用 · " + _session.AntiFadeError;
            if (!_config.DisableCameraFade) return _session.AntiFadeInstalled ? "正在还原" : "未启用";
            return _session.AntiFadeInstalled ? "已应用" : "等待应用";
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
            catch (Exception ex) { Log("复制日志失败：" + ex.Message); }
        }
    }
}

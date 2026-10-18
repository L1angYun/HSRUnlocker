using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using StarRailUnlocker.Core;

namespace StarRailUnlocker
{
    public sealed partial class MainWindow
    {
        private bool _updatingLanguage;

        private void CreateFpsPresets()
        {
            if (PresetPanel.Children.Count != 0) return;
            for (int i = 0; i < FpsPresets.Length; i++)
            {
                int value = FpsPresets[i];
                var button = new ToggleButton
                {
                    Content = value.ToString(), Tag = value, MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 6, 4, 6)
                };
                Grid.SetColumn(button, i);
                AutomationProperties.SetAutomationId(button, "FpsPreset" + value);
                button.Click += OnFpsPresetClick;
                PresetPanel.Children.Add(button);
            }
        }

        private void ApplyLocalization()
        {
            bool wasInitializing = _initializing;
            _initializing = true;
            _updatingLanguage = true;
            try
            {
                LayoutRoot.Language = L10n.Language;
                Title = L10n.T("Ui.WindowTitle", AppVersion);
                VersionText.Text = L10n.T("Ui.Version", AppVersion);
                PathLabel.Text = L10n.T("Ui.Client");
                ClientPathBox.PlaceholderText = L10n.T("Ui.ChooseExecutable");
                DisplaySectionTitle.Text = L10n.T("Ui.DisplaySection");
                FpsLabel.Text = L10n.T("Ui.TargetFramerate");
                FpsUnitText.Text = L10n.T("Ui.FpsUnit");
                FovLabel.Text = L10n.T("Ui.FovDelta");
                AntiFadeLabel.Text = L10n.T("Ui.AntiFade");
                StartupSectionTitle.Text = L10n.T("Ui.StartupSection");
                MobileUiLabel.Text = L10n.T("Ui.MobileUi");
                MobileUiHint.Text = L10n.T("Ui.NextGameLaunch");
                AutoStartLabel.Text = L10n.T("Ui.AutoStart");
                StartMinimizedLabel.Text = L10n.T("Ui.StartMinimized");
                ExitWithGameLabel.Text = L10n.T("Updates.ExitWithGame");
                UpdatesSectionTitle.Text = L10n.T("Updates.Section");
                CheckForUpdatesLabel.Text = L10n.T("Updates.CheckOnStartup");
                StartButtonText.Text = L10n.T("Ui.LaunchGame");
                AttachButtonText.Text = L10n.T("Ui.AttachGame");
                // WinUI caches selection text; reselect while change handlers are guarded.
                int graphicsSourceIndex = GraphicsSourceBox.SelectedIndex;
                GraphicsSourceBox.SelectedItem = null;
                GraphicsSourceBox.Header = WrappedText("Ui.GraphicsSource");
                GraphicsAutoItem.Content = L10n.T("Ui.GraphicsAuto");
                GraphicsChinaItem.Content = L10n.T("Ui.GraphicsChina");
                GraphicsGlobalItem.Content = L10n.T("Ui.GraphicsGlobal");
                GraphicsSourceBox.SelectedIndex = graphicsSourceIndex;
                int safeAreaModeIndex = SafeAreaModeBox.SelectedIndex;
                SafeAreaModeBox.SelectedItem = null;
                SafeAreaModeBox.Header = WrappedText("Ui.SafeAreaMode");
                SafeAreaOriginalItem.Content = L10n.T("Ui.SafeAreaOriginal");
                SafeAreaCustomItem.Content = L10n.T("Ui.SafeAreaCustom");
                SafeAreaModeBox.SelectedIndex = safeAreaModeIndex;
                SafeAreaHorizontalBox.Header = WrappedText("Ui.SafeAreaHorizontal");
                LogExpander.Header = WrappedText("Ui.RuntimeLog");
                LanguageBox.SelectedItem = null;
                LanguageBox.Header = WrappedText("Ui.Language");
                FontCreditText.Text = L10n.T("Ui.FontCredit");
                LanguageAutoItem.Content = L10n.T("Ui.LanguageAuto");
                foreach (var item in LanguageBox.Items)
                    if (item is ComboBoxItem choice && choice.Tag is string preference &&
                        preference == L10n.Preference)
                    {
                        LanguageBox.SelectedItem = choice;
                        break;
                    }

                LocalizeControl(SaveButton, "Ui.SaveConfig", "Ui.SaveConfig");
                LocalizeControl(ClientPathBox, "Ui.GamePath");
                LocalizeControl(BrowseButton, "Ui.ChooseExecutable", "Ui.ChooseExecutable");
                LocalizeControl(FpsSlider, "Ui.FpsSliderName", "Ui.FpsSliderTip");
                LocalizeControl(FpsBox, "Ui.FpsBoxName", "Ui.FpsBoxTip");
                LocalizeControl(FovSlider, "Ui.FovSliderName", "Ui.FovSliderTip");
                LocalizeControl(FovBox, "Ui.FovBoxName", "Ui.FovBoxTip");
                LocalizeControl(ResetFovButton, "Ui.ResetFov", "Ui.ResetFovTip");
                LocalizeControl(AntiFadeToggle, "Ui.AntiFade", "Ui.AntiFadeTip");
                LocalizeControl(MobileUiToggle, "Ui.MobileUi", "Ui.MobileUiTip");
                LocalizeControl(GraphicsSourceBox, "Ui.GraphicsSourceName", "Ui.GraphicsSourceTip");
                LocalizeControl(SafeAreaModeBox, "Ui.SafeAreaMode", "Ui.SafeAreaModeTip");
                LocalizeControl(SafeAreaHorizontalBox, "Ui.SafeAreaHorizontal", "Ui.SafeAreaHorizontalTip");
                LocalizeControl(AutoStartToggle, "Ui.AutoStart", "Ui.AutoStartTip");
                LocalizeControl(StartMinimizedToggle, "Ui.StartMinimized", "Ui.StartMinimizedTip");
                LocalizeControl(ExitWithGameToggle, "Updates.ExitWithGame", "Updates.ExitWithGameTip");
                LocalizeControl(CheckForUpdatesToggle, "Updates.CheckOnStartup", "Updates.CheckOnStartupTip");
                LocalizeControl(CheckUpdatesButton, "Updates.CheckNow", "Updates.CheckNow");
                LocalizeControl(OpenReleaseButton, "Updates.OpenRelease", "Updates.OpenRelease");
                LocalizeControl(ApplyUpdateButton, "Updates.Install", "Updates.InstallTip");
                LocalizeControl(CancelUpdateButton, "Updates.Cancel", "Updates.Cancel");
                LocalizeControl(UpdateProgressBar, "Updates.Progress");
                LocalizeControl(LanguageBox, "Ui.Language", "Ui.LanguageTip");
                LocalizeControl(LogExpander, "Ui.RuntimeLog");
                LocalizeControl(LogText, "Ui.RuntimeLogContent");
                LocalizeControl(CopyLogButton, "Ui.CopyLog", "Ui.CopyVisibleLog");
                LocalizeControl(ClearLogButton, "Ui.ClearVisibleLog", "Ui.ClearVisibleLog");
                LocalizeControl(StartButton, "Ui.LaunchGame", "Ui.LaunchGameTip");
                LocalizeControl(AttachButton, "Ui.AttachGame", "Ui.AttachGameTip");
                LocalizeControl(StopButton, "Ui.StopRestore", "Ui.StopRestoreTip");
                foreach (var child in PresetPanel.Children)
                    if (child is ToggleButton button && button.Tag is int value)
                    {
                        string text = L10n.T("Ui.FpsPreset", value);
                        AutomationProperties.SetName(button, text);
                        SetWrappedTooltip(button, text);
                    }
                RefreshUpdatePresentation();
                RefreshClientPresentation();
            }
            finally
            {
                _updatingLanguage = false;
                _initializing = wasInitializing;
            }
        }

        private static TextBlock WrappedText(string key) => new TextBlock
        {
            Text = L10n.T(key),
            TextWrapping = TextWrapping.Wrap
        };

        private static void LocalizeControl(DependencyObject control, string nameKey, string tooltipKey = null)
        {
            AutomationProperties.SetName(control, L10n.T(nameKey));
            if (tooltipKey != null) SetWrappedTooltip(control, L10n.T(tooltipKey));
        }

        private static void SetWrappedTooltip(DependencyObject control, string text) =>
            ToolTipService.SetToolTip(control, new ToolTip
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }
            });

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || _updatingLanguage || _closed || _closePending ||
                LanguageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string preference)
                return;
            preference = L10n.NormalizePreference(preference);
            if (preference == L10n.Preference) return;
            _config.Language = preference;
            L10n.SetLanguage(preference);
            ApplyLocalization();
            RefreshPresentation();
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
        }

        private static string SessionStateText(SessionState state) => L10n.T(state switch
        {
            SessionState.WaitingForGame => "Ui.StateWaiting",
            SessionState.Patching => "Ui.StateAttaching",
            SessionState.Active => "Ui.StateActive",
            SessionState.Failed => "Ui.StateFailed",
            SessionState.GameExited => "Ui.StateGameExited",
            _ => "Ui.StateIdle"
        });
    }
}

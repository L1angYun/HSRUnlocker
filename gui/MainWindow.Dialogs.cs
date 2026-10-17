using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using StarRailUnlocker.Core;

namespace StarRailUnlocker
{
    public sealed partial class MainWindow
    {
        private async Task ShowSetupAsync()
        {
            if (_closed || _closePending || _dialogOpen || _pickingGame || _dialogRoot == null) return;
            _setupInProgress = true;
            _dialogOpen = true;
            try
            {
                var dialog = CreateSetupDialog(_dialogRoot, _detectedGamePaths, out ListView list);
                if (_detectedGamePaths.Count == 0) Log(L10n.T("Ui.LogNoAutoDetectedGame"));
                var result = await dialog.ShowAsync();
                if (_closed || _closePending) return;
                if (list != null && result == ContentDialogResult.Primary && list.SelectedIndex >= 0)
                {
                    string path = _detectedGamePaths[list.SelectedIndex];
                    if (IsValidGamePath(path))
                    {
                        PathBox.Text = _config.ProcessPath = path;
                        _localErrorKey = null;
                        if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
                        Log(L10n.T("Ui.LogGamePathSet", path));
                    }
                    else Log(L10n.T("Ui.LogInvalidGamePath"));
                }
                else if ((list == null && result == ContentDialogResult.Primary) ||
                    (list != null && result == ContentDialogResult.Secondary))
                    await ChooseGameAsync();
            }
            catch (Exception ex) { App.WriteLog("First-run setup", ex); }
            finally { _dialogOpen = false; }
        }

        internal static ContentDialog CreateSetupDialog(XamlRoot root, IReadOnlyList<string> paths, out ListView list)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = L10n.T("Ui.SetupTitle"),
                CloseButtonText = L10n.T("Ui.SetupLater"),
                DefaultButton = ContentDialogButton.Close
            };
            list = null;
            if (paths.Count == 0)
            {
                dialog.Content = new TextBlock { Text = L10n.T("Ui.SetupContent"), TextWrapping = TextWrapping.Wrap };
                dialog.PrimaryButtonText = L10n.T("Ui.ChooseExecutable");
                return dialog;
            }
            var choices = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
            AutomationProperties.SetName(choices, L10n.T("Ui.DetectedClients"));
            foreach (string path in paths)
                choices.Items.Add(new TextBlock
                {
                    Text = path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
                });
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = L10n.T("Ui.MultipleClients"), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(choices);
            dialog.Content = content;
            dialog.PrimaryButtonText = L10n.T("Ui.UseSelectedClient");
            dialog.SecondaryButtonText = L10n.T("Ui.Browse");
            dialog.IsPrimaryButtonEnabled = false;
            choices.SelectionChanged += (_, __) => dialog.IsPrimaryButtonEnabled = choices.SelectedIndex >= 0;
            list = choices;
            return dialog;
        }

        private void QueueUpdatePrompt()
        {
            if (!_closed && !_closePending)
                DispatcherQueue.TryEnqueue(() => _ = ShowUpdatePromptAsync());
        }

        private async Task ShowUpdatePromptAsync()
        {
            // Keep setup, file picking, and tray restoration ahead of background notifications.
            UpdateCheckResult result = _updatePrompts.Take(_startupComplete && _dialogRoot != null &&
                !_closed && !_closePending && !_dialogOpen && !_pickingGame && !_inTray &&
                !_checkingForUpdates && !_downloadingUpdate && !_openingRelease);
            if (result == null) return;
            _dialogOpen = true;
            ContentDialogResult action = ContentDialogResult.None;
            try { action = await CreateUpdateDialog(_dialogRoot, result).ShowAsync(); }
            catch (Exception ex) { App.WriteLog("Update notification", ex); }
            finally { _dialogOpen = false; }
            if (_closed || _closePending || !ReferenceEquals(result, _updateResult)) return;
            if (action == ContentDialogResult.Primary && result.Installer != null)
                await ApplyUpdateAsync();
            else if (action == ContentDialogResult.Secondary ||
                (action == ContentDialogResult.Primary && result.Installer == null))
                await OpenReleaseAsync();
            QueueUpdatePrompt();
        }

        internal static ContentDialog CreateUpdateDialog(XamlRoot root, UpdateCheckResult result) => new()
        {
            XamlRoot = root,
            Title = L10n.T("Updates.PromptTitle", result.Version),
            Content = new TextBlock
            {
                Text = L10n.T(result.Installer != null ? "Updates.PromptContent" : "Updates.NoInstaller", result.Version),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = L10n.T(result.Installer != null ? "Updates.PromptInstall" : "Updates.PromptNotes"),
            SecondaryButtonText = result.Installer != null ? L10n.T("Updates.PromptNotes") : string.Empty,
            CloseButtonText = L10n.T("Updates.Later"),
            DefaultButton = ContentDialogButton.Close
        };
    }
}

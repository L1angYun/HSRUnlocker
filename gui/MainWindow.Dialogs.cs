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
                    TrySetGamePath(path);
                }
                else if ((list == null && result == ContentDialogResult.Primary) ||
                    (list != null && result == ContentDialogResult.Secondary))
                    await ChooseGameAsync(duringSetup: true);
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

    }
}

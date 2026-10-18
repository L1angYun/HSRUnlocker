using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StarRailUnlocker.Core;

namespace StarRailUnlocker
{
    public sealed partial class MainWindow
    {
        private bool _scanningGames, _syncingGamePaths;
        private string _clientScanStatusKey;
        private int _availableClientCount;

        private bool CanChangeGameClient =>
            !_initializing && !_closed && !_closePending && !_switchingSession &&
            !_setupInProgress && !_dialogOpen && !_pickingGame && !_scanningGames &&
            !_downloadingUpdate && _session?.IsRunning != true;

        private void RefreshGameClients(IEnumerable<string> discovered = null)
        {
            string selected = GameClientPaths.Normalize(new[] { _config.ProcessPath }).FirstOrDefault();
            var paths = GameClientPaths.Available(_config.ProcessPath, _config.GamePaths,
                discovered ?? Array.Empty<string>(), IsValidGamePath);
            _config.GamePaths = paths;
            _detectedGamePaths = paths;
            _availableClientCount = paths.Length;
            _syncingGamePaths = true;
            try
            {
                ClientPathBox.ItemsSource = paths;
                ClientPathBox.SelectedItem = paths.FirstOrDefault(path =>
                    string.Equals(path, selected, StringComparison.OrdinalIgnoreCase));
                SetWrappedTooltip(ClientPathBox, _config.ProcessPath);
            }
            finally { _syncingGamePaths = false; }
        }

        private bool TrySetGamePath(string path)
        {
            if (_closed || _closePending) return false;
            path = GameClientPaths.Normalize(new[] { path }).FirstOrDefault();
            if (!IsValidGamePath(path))
            {
                Log(L10n.T("Ui.LogInvalidGamePath"));
                RefreshGameClients();
                return false;
            }
            _config.ProcessPath = path;
            _localErrorKey = null;
            _clientScanStatusKey = null;
            RefreshGameClients();
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            Log(L10n.T("Ui.LogGamePathSet", path));
            RefreshPresentation();
            return true;
        }

        private void OnGameClientChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingGamePaths || _initializing) return;
            if (!CanChangeGameClient)
            {
                RefreshGameClients();
                return;
            }
            if (ClientPathBox.SelectedItem is string path &&
                !string.Equals(path, _config.ProcessPath, StringComparison.OrdinalIgnoreCase))
                TrySetGamePath(path);
        }

        private async void OnScanGamesClick(object sender, RoutedEventArgs e)
        {
            if (CanChangeGameClient) await ScanGameClientsAsync();
        }

        private async Task ScanGameClientsAsync(bool duringSetup = false)
        {
            if (_closed || _closePending || _scanningGames || _pickingGame || _switchingSession ||
                _dialogOpen || _downloadingUpdate || _session?.IsRunning == true ||
                (_setupInProgress && !duringSetup)) return;
            _scanningGames = true;
            _clientScanStatusKey = "Ui.ScanningClients";
            RefreshPresentation();
            try
            {
                var installed = await Task.Run(GameInstallationDiscovery.FindInstalled);
                if (_closed || _closePending) return;
                var paths = new List<string>(installed);
                paths.AddRange(FindRunningGames().Select(game => game.Path));
                RefreshGameClients(paths);
                _clientScanStatusKey = _availableClientCount == 0 ? "Ui.NoClientsFound" : "Ui.AvailableClients";
                if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            }
            catch (Exception ex)
            {
                if (_closed || _closePending) return;
                _clientScanStatusKey = "Ui.ClientScanFailed";
                Log(L10n.T("Ui.LogClientScanFailed", ex.Message));
                App.WriteLog("Game client scan", ex);
            }
            finally
            {
                _scanningGames = false;
                if (!_closed && !_closePending) RefreshPresentation();
                QueueUpdatePrompt();
            }
        }

        private void RefreshClientPresentation()
        {
            ClientPathBox.IsEnabled = ScanGamesButton.IsEnabled = BrowseButton.IsEnabled = CanChangeGameClient;
            ClientScanProgress.IsActive = _scanningGames;
            ClientScanProgress.Visibility = _scanningGames ? Visibility.Visible : Visibility.Collapsed;
            ClientScanStatus.Visibility = _clientScanStatusKey == null ? Visibility.Collapsed : Visibility.Visible;
            ClientScanStatus.Text = _clientScanStatusKey == null ? string.Empty :
                L10n.T(_clientScanStatusKey, _availableClientCount);
            LocalizeControl(ScanGamesButton, _scanningGames ? "Ui.ScanningClients" : "Ui.ScanClients",
                _scanningGames ? "Ui.ScanningClients" : "Ui.ScanClients");
        }
    }
}

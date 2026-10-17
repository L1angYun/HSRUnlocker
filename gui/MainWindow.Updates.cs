using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using StarRailUnlocker.Core;

namespace StarRailUnlocker
{
    public sealed partial class MainWindow
    {
        private readonly HttpClient _updateHttp = new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
        });
        private readonly HttpClient _downloadHttp = new(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            UseDefaultCredentials = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        private readonly UpdateChecker _updateChecker;
        private CancellationTokenSource _updateCancellation = new();
        private CancellationTokenSource _downloadCancellation;
        private PreparedUpdateInstall _preparedUpdate;
        private UpdateCheckResult _updateResult;
        private readonly UpdatePromptQueue _updatePrompts = new();
        private string _updateStatusKey = "Updates.NotChecked";
        private bool _checkingForUpdates, _openingRelease, _downloadingUpdate;
        private double _updateProgress;

        private void OnCheckForUpdatesToggled(object sender, RoutedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            _config.CheckForUpdates = CheckForUpdatesToggle.IsOn;
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
        }

        private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

        private async Task CheckUpdatesAsync()
        {
            if (_checkingForUpdates || _downloadingUpdate || _closed || _closePending) return;
            _checkingForUpdates = true;
            _updateStatusKey = "Updates.Checking";
            RefreshUpdatePresentation();
            CancellationToken token = _updateCancellation.Token;
            try
            {
                var result = await _updateChecker.CheckAsync(AppVersion, token);
                if (_closed || _closePending) return;
                _updateResult = result;
                _updatePrompts.Offer(result);
                _updateStatusKey = result.IsNewer
                    ? result.Installer != null ? "Updates.Available" : "Updates.NoInstaller"
                    : "Updates.Current";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (UpdateRateLimitException ex)
            {
                if (_closed || _closePending) return;
                _updateStatusKey = "Updates.RateLimited";
                App.WriteLog("Update check", ex);
            }
            catch (OperationCanceledException ex)
            {
                if (_closed || _closePending) return;
                _updateStatusKey = "Updates.TimedOut";
                App.WriteLog("Update check", ex);
            }
            catch (Exception ex)
            {
                if (_closed || _closePending) return;
                _updateStatusKey = "Updates.Failed";
                App.WriteLog("Update check", ex);
            }
            finally
            {
                _checkingForUpdates = false;
                if (!_closed && !_closePending) RefreshUpdatePresentation();
                QueueUpdatePrompt();
            }
        }

        private async void OnApplyUpdateClick(object sender, RoutedEventArgs e) => await ApplyUpdateAsync();

        private async Task ApplyUpdateAsync()
        {
            if (_closed || _closePending || _downloadingUpdate || _checkingForUpdates ||
                _updateResult?.IsNewer != true || _updateResult.Installer == null) return;
            _downloadingUpdate = true;
            _updateProgress = 0;
            _updateStatusKey = "Updates.Downloading";
            CancelGameExitClose();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            _downloadCancellation = cancellation;
            RefreshUpdatePresentation();
            try
            {
                var installer = new UpdateInstaller(_downloadHttp);
                var progress = new Progress<double>(value =>
                {
                    if (_closed || _closePending || !_downloadingUpdate) return;
                    _updateProgress = Math.Clamp(value, 0, 1);
                    RefreshUpdatePresentation();
                });
                using StagedUpdate staged = await installer.DownloadAsync(
                    _updateResult.Installer, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _updateStatusKey = "Updates.Preparing";
                RefreshUpdatePresentation();
                var prepared = await installer.PrepareInstallAsync(
                    staged, L10n.T("Updates.HelperFailed"), cancellation.Token);
                if (_closed || _closePending || cancellation.IsCancellationRequested)
                {
                    prepared.Dispose();
                    return;
                }
                _preparedUpdate = prepared;
                BeginCloseAsync();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!_closed && !_closePending) _updateStatusKey = "Updates.Cancelled";
            }
            catch (Exception ex)
            {
                if (!_closed && !_closePending)
                {
                    _updateStatusKey = "Updates.InstallFailed";
                    App.WriteLog("Prepare update", ex);
                }
            }
            finally
            {
                _downloadCancellation = null;
                _downloadingUpdate = false;
                if (!_closed && !_closePending)
                {
                    RefreshUpdatePresentation();
                    if (_config.ExitWithGame && _session?.State == SessionState.GameExited)
                        ScheduleGameExitClose(_session);
                }
            }
        }

        private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

        private async void OnOpenReleaseClick(object sender, RoutedEventArgs e) => await OpenReleaseAsync();

        private async Task OpenReleaseAsync()
        {
            if (_closed || _closePending || _openingRelease || _downloadingUpdate ||
                _updateResult?.IsNewer != true) return;
            _openingRelease = true;
            RefreshUpdatePresentation();
            try
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(_updateResult.ReleaseUri))
                    _updateStatusKey = "Updates.OpenFailed";
            }
            catch (Exception ex)
            {
                _updateStatusKey = "Updates.OpenFailed";
                App.WriteLog("Open release", ex);
            }
            finally
            {
                _openingRelease = false;
                if (!_closed && !_closePending) RefreshUpdatePresentation();
            }
        }

        private void RefreshUpdatePresentation()
        {
            if (_closed || UpdateStatusText == null) return;
            bool available = _updateResult?.IsNewer == true;
            object statusValue = _updateStatusKey == "Updates.Downloading" ? _updateProgress :
                _updateStatusKey == "Updates.Current" ? AppVersion : _updateResult?.Version;
            UpdateStatusText.Text = L10n.T(_updateStatusKey, statusValue);
            CheckForUpdatesToggle.IsEnabled = !_closePending;
            CheckUpdatesButton.IsEnabled = !_closePending && !_checkingForUpdates && !_openingRelease && !_downloadingUpdate;
            OpenReleaseButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            OpenReleaseButton.IsEnabled = available && !_closePending && !_checkingForUpdates && !_openingRelease && !_downloadingUpdate;
            ApplyUpdateButton.Visibility = available && _updateResult.Installer != null && !_downloadingUpdate
                ? Visibility.Visible : Visibility.Collapsed;
            ApplyUpdateButton.IsEnabled = !_closePending && !_checkingForUpdates && !_openingRelease;
            CancelUpdateButton.Visibility = _downloadingUpdate && !_closePending ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgressBar.Visibility = _downloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgressBar.IsIndeterminate = _updateStatusKey == "Updates.Preparing";
            UpdateProgressBar.Value = _updateProgress * 100;
        }

        private void AbortPreparedUpdate()
        {
            _preparedUpdate?.Dispose();
            _preparedUpdate = null;
        }

        private void CommitPreparedUpdate()
        {
            _preparedUpdate?.Detach();
            _preparedUpdate?.Dispose();
            _preparedUpdate = null;
        }

        private void DisposeUpdateCheck()
        {
            AbortPreparedUpdate();
            _updateCancellation.Cancel();
            _updateHttp.Dispose();
            _downloadHttp.Dispose();
            _updateCancellation.Dispose();
        }
    }
}

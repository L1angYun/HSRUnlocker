using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        private readonly UpdateChecker _updateChecker;
        private CancellationTokenSource _updateCancellation = new();
        private CancellationTokenSource _downloadCancellation;
        private PreparedUpdateInstall _preparedUpdate;
        private UpdateCheckResult _updateResult;
        private readonly UpdateNotice _updateNotice = new();
        private string _updateStatusKey = "Updates.NotChecked";
        private bool _checkingForUpdates, _openingRelease, _downloadingUpdate;
        private double _updateProgress;

        private void OnUpdateSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || _updatingLanguage || _closed || _closePending ||
                _checkingForUpdates || _openingRelease || _downloadingUpdate || _preparedUpdate != null ||
                !UpdateSourceBox.IsEnabled || UpdateSourceBox.SelectedIndex is < 0 or > 2)
                return;
            var preference = (UpdateSourcePreference)UpdateSourceBox.SelectedIndex;
            if (_config.UpdateSource == preference) return;
            _config.UpdateSource = preference;
            _updateResult = null;
            _updateStatusKey = "Updates.NotChecked";
            _updateNotice.Offer(null, background: false);
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            RefreshUpdatePresentation();
        }

        private void OnCheckForUpdatesToggled(object sender, RoutedEventArgs e)
        {
            if (_initializing || _closed || _closePending) return;
            _config.CheckForUpdates = CheckForUpdatesToggle.IsOn;
            if (!_config.CheckForUpdates) _updateNotice.Offer(null, background: false);
            if (!_config.Save()) Log(L10n.T("Ui.LogConfigSaveFailed", UnlockConfig.LogPath));
            RefreshUpdatePresentation();
        }

        private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

        private async Task CheckUpdatesAsync(bool background = false)
        {
            if (_checkingForUpdates || _downloadingUpdate || _closed || _closePending) return;
            _checkingForUpdates = true;
            _updateResult = null;
            _updateNotice.Offer(null, background);
            _updateStatusKey = "Updates.Checking";
            RefreshUpdatePresentation();
            CancellationToken token = _updateCancellation.Token;
            try
            {
                var result = await _updateChecker.CheckAsync(AppVersion, _config.UpdateSource, token);
                if (_closed || _closePending) return;
                _updateResult = result;
                _updateNotice.Offer(result, background && _config.CheckForUpdates);
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
            }
        }

        private void OnReviewUpdateNotice(object sender, RoutedEventArgs e)
        {
            _updateNotice.Dismiss();
            RefreshUpdatePresentation();
            UpdatesSection.StartBringIntoView();
            if (ApplyUpdateButton.IsEnabled && ApplyUpdateButton.Visibility == Visibility.Visible)
                ApplyUpdateButton.Focus(FocusState.Programmatic);
            else
                CheckUpdatesButton.Focus(FocusState.Programmatic);
        }

        private void OnUpdateNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
        {
            if (args.Reason == InfoBarCloseReason.CloseButton) _updateNotice.Dismiss();
        }

        private bool UpdateInstallNeedsIdle => !_startupComplete || _switchingSession
            || _session?.IsRunning == true;

        private async void OnApplyUpdateClick(object sender, RoutedEventArgs e) => await ApplyUpdateAsync();

        private async Task ApplyUpdateAsync()
        {
            if (_closed || _closePending || _dialogOpen || _pickingGame || _openingRelease ||
                _downloadingUpdate || _checkingForUpdates || UpdateInstallNeedsIdle ||
                _updateResult?.IsNewer != true || _updateResult.Installer == null) return;
            _downloadingUpdate = true;
            _updateNotice.Dismiss();
            _updateProgress = 0;
            _updateStatusKey = "Updates.Downloading";
            CancelGameExitClose();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            _downloadCancellation = cancellation;
            RefreshUpdatePresentation();
            bool downloaded = false;
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
                downloaded = true;
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
                    _updateStatusKey = downloaded ? "Updates.InstallFailed" : "Updates.DownloadFailed";
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
            bool canInstall = available && _updateResult.Installer != null;
            bool unblocked = !_closePending && !_dialogOpen && !_pickingGame
                && !_checkingForUpdates && !_openingRelease && !_downloadingUpdate;
            object statusValue = _updateStatusKey == "Updates.Downloading" ? _updateProgress :
                _updateStatusKey == "Updates.Current" ? AppVersion : _updateResult?.Version;
            UpdateStatusText.Text = L10n.T(_updateStatusKey, statusValue);
            UpdateVersionText.Text = L10n.T("Updates.InstalledVersion", AppVersion);
            UpdateInstallHint.Text = L10n.T("Updates.InstallWhenIdle");
            UpdateInstallHint.Visibility = canInstall && UpdateInstallNeedsIdle && !_downloadingUpdate
                ? Visibility.Visible : Visibility.Collapsed;
            var startupUpdate = _updateNotice.Visible(_startupComplete && !_inTray && !_closePending
                && !_dialogOpen && !_pickingGame && !_scanningGames && !_switchingSession
                && _session?.IsRunning != true && !SessionFailureInfoBar.IsOpen && unblocked);
            if (startupUpdate != null)
            {
                StartupUpdateNotice.Title = L10n.T("Updates.PromptTitle", startupUpdate.Version);
                StartupUpdateNotice.Message = startupUpdate.Installer != null
                    ? L10n.T("Updates.StartupNotice") : L10n.T("Updates.NoInstaller", startupUpdate.Version);
            }
            StartupUpdateNotice.IsOpen = startupUpdate != null;
            CheckForUpdatesToggle.IsEnabled = !_closePending;
            UpdateSourceBox.IsEnabled = unblocked && _preparedUpdate == null;
            CheckUpdatesButton.IsEnabled = unblocked;
            OpenReleaseButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            OpenReleaseButton.IsEnabled = available && unblocked;
            ApplyUpdateButton.Visibility = canInstall && !_downloadingUpdate
                ? Visibility.Visible : Visibility.Collapsed;
            ApplyUpdateButton.IsEnabled = canInstall && unblocked && !UpdateInstallNeedsIdle;
            ApplyUpdateButton.Content = WrappedText(_updateStatusKey is "Updates.InstallFailed" or "Updates.DownloadFailed"
                ? "Updates.RetryInstall" : "Updates.Install");
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

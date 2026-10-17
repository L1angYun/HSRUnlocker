using System;
using Microsoft.UI.Xaml;
using StarRailUnlocker.Core;

namespace StarRailUnlocker
{
    public partial class App : Application
    {
        private Window _window;
        private readonly System.Threading.Mutex _instanceGuard;
        private bool _ownsInstance;

        public App()
        {
            UnhandledException += (_, e) =>
            {
                WriteLog("Application.UnhandledException", e.Exception);
                e.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            _instanceGuard = new System.Threading.Mutex(true, "HSRUnlocker", out _ownsInstance);
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                if (!_ownsInstance) { TrayIcon.RestoreExisting(); _instanceGuard.Dispose(); Exit(); return; }
                if (_window != null) { _window.Activate(); return; }
                var window = new MainWindow();
                window.Closed += (_, __) => { if (_ownsInstance) { _instanceGuard.ReleaseMutex(); _ownsInstance = false; _instanceGuard.Dispose(); } };
                _window = window;
                window.Activate();
                window.OnOpened();
            }
            catch (Exception ex)
            {
                WriteLog("OnLaunched / MainWindow", ex);
                throw;
            }
        }

        internal static void WriteLog(string stage, string message)
        {
            UnlockConfig.AppendLog(stage, message);
        }

        internal static void WriteLog(string stage, Exception ex)
        {
            UnlockConfig.AppendLog(stage, ex?.Message ?? "(none)", ex);
        }
    }
}

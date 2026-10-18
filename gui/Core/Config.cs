using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarRailUnlocker.Core
{
    /// <summary>
    /// Settings owned by HSRUnlocker.
    ///
    /// The current file is always under the user's local application data directory:
    /// %LOCALAPPDATA%\HSRUnlocker\settings.json.  The only legacy source considered is
    /// hsr_config.json beside the old tool executable.  Game installation directories are
    /// never used for settings or diagnostics.
    /// </summary>
    public sealed class UnlockConfig
    {
        private const int FpsMin = 10;
        private const int FpsMax = 480;
        private const double FovMin = -60.0;
        private const double FovMax = 60.0;
        private const double SafeAreaHorizontalDefault = 5.0;
        private const double SafeAreaMaxPercent = 15.0;

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly object LogSync = new object();

        public string ProcessPath { get; set; } = "";
        public string[] GamePaths { get; set; } = Array.Empty<string>();
        public int TargetFramerate { get; set; } = 120;
        public double FovDelta { get; set; } = 0.0;
        public bool DisableCameraFade { get; set; } = false;
        public bool AutoStart { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool ExitWithGame { get; set; } = true;
        public bool CheckForUpdates { get; set; } = true;
        public UpdateSourcePreference UpdateSource { get; set; } = UpdateSourcePreference.Auto;
        public bool UseMobileUI { get; set; } = false;
        public MobileGraphicsSource GraphicsSource { get; set; } = MobileGraphicsSource.Auto;
        public MobileSafeAreaMode SafeAreaMode { get; set; } = MobileSafeAreaMode.Original;
        public double SafeAreaHorizontalPercent { get; set; } = SafeAreaHorizontalDefault;
        public string Language { get; set; } = "auto";

        /// <summary>Directory owned by this tool for settings and diagnostics.</summary>
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "HSRUnlocker");

        /// <summary>Current settings path; never resolve this from a game path.</summary>
        public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");

        /// <summary>The pre-migration settings path used by the old tool executable.</summary>
        public static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "hsr_config.json");

        /// <summary>Persistent UTF-8 diagnostics that can be inspected after a failed launch.</summary>
        public static string LogPath => Path.Combine(DataDirectory, "HSRUnlocker.log");

        /// <summary>
        /// Load current settings, recover their backup, or import a legacy settings file
        /// beside the tool executable into the tool-owned directory.
        /// </summary>
        public static UnlockConfig Load() => CreateStorage().Load();

        /// <summary>Save settings to the tool-owned local application data directory.</summary>
        public bool Save() => CreateStorage().Save(this);

        private static ConfigStorage CreateStorage() => new ConfigStorage(DefaultPath, LegacyPath, AppendLog);

        /// <summary>Keep values loaded from older versions within the UI's supported range.</summary>
        public void Normalize()
        {
            ProcessPath = (ProcessPath ?? string.Empty).Trim();
            GamePaths = GameClientPaths.Normalize((GamePaths ?? Array.Empty<string>()).Prepend(ProcessPath));
            TargetFramerate = Math.Clamp(TargetFramerate, FpsMin, FpsMax);
            if (double.IsNaN(FovDelta) || double.IsInfinity(FovDelta))
                FovDelta = 0.0;
            FovDelta = Math.Clamp(FovDelta, FovMin, FovMax);
            if (!Enum.IsDefined(GraphicsSource)) GraphicsSource = MobileGraphicsSource.Auto;
            if (!Enum.IsDefined(SafeAreaMode)) SafeAreaMode = MobileSafeAreaMode.Original;
            if (!Enum.IsDefined(UpdateSource)) UpdateSource = UpdateSourcePreference.Auto;
            if (!double.IsFinite(SafeAreaHorizontalPercent)) SafeAreaHorizontalPercent = SafeAreaHorizontalDefault;
            SafeAreaHorizontalPercent = Math.Clamp(SafeAreaHorizontalPercent, 0, SafeAreaMaxPercent);
            Language = L10n.NormalizePreference(Language);
        }

        public UnlockConfig Clone() => new UnlockConfig
        {
            ProcessPath = ProcessPath,
            GamePaths = GamePaths?.ToArray() ?? Array.Empty<string>(),
            TargetFramerate = TargetFramerate,
            FovDelta = FovDelta,
            DisableCameraFade = DisableCameraFade,
            AutoStart = AutoStart,
            StartMinimized = StartMinimized,
            ExitWithGame = ExitWithGame,
            CheckForUpdates = CheckForUpdates,
            UpdateSource = UpdateSource,
            UseMobileUI = UseMobileUI,
            GraphicsSource = GraphicsSource,
            SafeAreaMode = SafeAreaMode,
            SafeAreaHorizontalPercent = SafeAreaHorizontalPercent,
            Language = Language,
        };

        internal static void AppendLog(string stage, string message, Exception ex = null)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("================================================================");
                sb.AppendLine("time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("stage   : " + (stage ?? "(unknown)"));
                sb.AppendLine("message : " + (message ?? "(none)"));
                if (ex != null)
                {
                    sb.AppendLine("type    : " + ex.GetType().FullName);
                    sb.AppendLine("hresult : 0x" + ex.HResult.ToString("X8"));
                    sb.AppendLine(ex.ToString());
                }

                Directory.CreateDirectory(DataDirectory);
                lock (LogSync)
                    File.AppendAllText(LogPath, sb.ToString(), Utf8);
            }
            catch
            {
                // Diagnostics must never prevent the UI from starting.
            }
        }
    }

    /// <summary>Settings storage bound to one primary path and one legacy import source.</summary>
    internal sealed class ConfigStorage
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly string settingsPath;
        private readonly string legacyPath;
        private readonly Action<string, string, Exception> diagnostic;
        private readonly Action<string, string, string> replaceFile;

        internal ConfigStorage(string settingsPath, string legacyPath, Action<string, string, Exception> diagnostic,
            Action<string, string, string> replaceFile = null)
        {
            this.settingsPath = Path.GetFullPath(settingsPath);
            this.legacyPath = Path.GetFullPath(legacyPath);
            this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
            this.replaceFile = replaceFile ?? File.Replace;
        }

        internal UnlockConfig Load()
        {
            if (TryLoad(settingsPath, out var current))
            {
                current.Normalize();
                return current;
            }

            if (TryLoad(settingsPath + ".bak", out var backup))
            {
                backup.Normalize();
                diagnostic("Config recovery", L10n.T("Config.BackupRecovered", settingsPath + ".bak"), null);
                return backup;
            }

            if (TryLoad(legacyPath, out var legacy))
            {
                legacy.Normalize();
                if (Save(legacy))
                    diagnostic("Config migration", L10n.T("Config.Migrated", settingsPath), null);
                else
                    diagnostic("Config migration", L10n.T("Config.MigrationSaveFailed", settingsPath), null);
                return legacy;
            }

            return new UnlockConfig();
        }

        internal bool Save(UnlockConfig config)
        {
            config.Normalize();
            string temporaryPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string primarySnapshot = temporaryPath + ".primary";
            string backupSnapshot = temporaryPath + ".backup";
            string backupPath = settingsPath + ".bak";
            bool ownsTemporary = false, ownsPrimary = false, ownsBackup = false;
            bool replacementStarted = false, backupCanChange = false, retainRecovery = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    JsonSerializer.Serialize(stream, config, Opts);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(settingsPath))
                {
                    CopySnapshot(settingsPath, primarySnapshot, ref ownsPrimary);
                    // Only a deserializable primary may replace the last-known-good backup.
                    // Read failures must abort rather than treating an inaccessible file as corrupt.
                    bool validPrimary;
                    try { validPrimary = JsonSerializer.Deserialize<UnlockConfig>(File.ReadAllText(primarySnapshot, Utf8), Opts) != null; }
                    catch (JsonException) { validPrimary = false; }
                    backupCanChange = validPrimary;
                    if (backupCanChange)
                    {
                        try { CopySnapshot(backupPath, backupSnapshot, ref ownsBackup); }
                        catch (FileNotFoundException) { }
                    }
                    replacementStarted = true;
                    replaceFile(temporaryPath, settingsPath, validPrimary ? backupPath : null);
                }
                else
                    File.Move(temporaryPath, settingsPath);
                return true;
            }
            catch (Exception ex)
            {
                if (replacementStarted)
                {
                    // ReplaceFile can rename/delete originals before reporting a late failure.
                    bool restoredPrimary = RestoreSnapshot(primarySnapshot, settingsPath);
                    bool restoredBackup = true;
                    if (backupCanChange)
                    {
                        if (ownsBackup)
                            restoredBackup = RestoreSnapshot(backupSnapshot, backupPath);
                        else
                        {
                            try { File.Delete(backupPath); }
                            catch (Exception rollbackError)
                            {
                                restoredBackup = false;
                                diagnostic("Config rollback", L10n.T("Config.RollbackFailed", backupPath), rollbackError);
                            }
                        }
                    }
                    retainRecovery = !restoredPrimary || !restoredBackup;
                }
                diagnostic("Config save", L10n.T("Config.SaveFailed", settingsPath), ex);
                return false;
            }
            finally
            {
                if (!retainRecovery)
                {
                    CleanupTemporary(temporaryPath, ownsTemporary);
                    CleanupTemporary(primarySnapshot, ownsPrimary);
                    CleanupTemporary(backupSnapshot, ownsBackup);
                }
            }
        }

        private static void CopySnapshot(string source, string snapshot, ref bool owned)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned = true;
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        private bool RestoreSnapshot(string snapshot, string destination)
        {
            try
            {
                if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(snapshot)))
                    return true;
                File.Move(snapshot, destination, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                diagnostic("Config rollback", L10n.T("Config.RollbackFailed", snapshot), ex);
                return false;
            }
        }

        private void CleanupTemporary(string path, bool owned)
        {
            if (!owned) return;
            try { File.Delete(path); }
            catch (Exception ex) { diagnostic("Config cleanup", L10n.T("Config.SaveFailed", path), ex); }
        }

        private bool TryLoad(string path, out UnlockConfig config)
        {
            config = null;
            if (!File.Exists(path)) return false;

            try
            {
                var json = File.ReadAllText(path, Utf8);
                config = JsonSerializer.Deserialize<UnlockConfig>(json, Opts);
                if (config != null) return true;
                diagnostic("Config load", L10n.T("Config.Empty", path), null);
            }
            catch (Exception ex)
            {
                diagnostic("Config load", L10n.T("Config.LoadFailed", path), ex);
            }

            config = null;
            return false;
        }
    }
}

using System;
using System.IO;
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

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly object LogSync = new object();

        public string ProcessPath { get; set; } = "";
        public int TargetFramerate { get; set; } = 120;
        public double FovDelta { get; set; } = 0.0;
        public bool DisableCameraFade { get; set; } = false;
        public bool AutoStart { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool UseMobileUI { get; set; } = false;
        public MobileGraphicsSource GraphicsSource { get; set; } = MobileGraphicsSource.Auto;

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
        /// Load the tool settings.  Existing settings win; otherwise a legacy settings file
        /// beside the tool executable is imported once into the tool-owned directory.
        /// </summary>
        public static UnlockConfig Load()
        {
            if (TryLoad(DefaultPath, out var current))
            {
                current.Normalize();
                return current;
            }

            if (TryLoad(LegacyPath, out var legacy))
            {
                legacy.Normalize();
                if (legacy.Save())
                    AppendLog("Config migration", "Migrated legacy settings to " + DefaultPath);
                else
                    AppendLog("Config migration", "Loaded legacy settings but could not write " + DefaultPath);
                return legacy;
            }

            return new UnlockConfig();
        }

        /// <summary>Save settings to the tool-owned local application data directory.</summary>
        public bool Save()
        {
            Normalize();
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, Opts), Utf8);
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("Config save", "Could not write " + DefaultPath, ex);
                return false;
            }
        }

        /// <summary>Keep values loaded from older versions within the UI's supported range.</summary>
        public void Normalize()
        {
            ProcessPath = (ProcessPath ?? string.Empty).Trim();
            TargetFramerate = Math.Clamp(TargetFramerate, FpsMin, FpsMax);
            if (double.IsNaN(FovDelta) || double.IsInfinity(FovDelta))
                FovDelta = 0.0;
            FovDelta = Math.Clamp(FovDelta, FovMin, FovMax);
            if (!Enum.IsDefined(GraphicsSource)) GraphicsSource = MobileGraphicsSource.Auto;
        }

        public UnlockConfig Clone() => new UnlockConfig
        {
            ProcessPath = ProcessPath,
            TargetFramerate = TargetFramerate,
            FovDelta = FovDelta,
            DisableCameraFade = DisableCameraFade,
            AutoStart = AutoStart,
            StartMinimized = StartMinimized,
            UseMobileUI = UseMobileUI,
            GraphicsSource = GraphicsSource,
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

        private static bool TryLoad(string path, out UnlockConfig config)
        {
            config = null;
            if (!File.Exists(path)) return false;

            try
            {
                var json = File.ReadAllText(path, Utf8);
                config = JsonSerializer.Deserialize<UnlockConfig>(json, Opts);
                if (config != null) return true;
                AppendLog("Config load", "Settings file was empty: " + path);
            }
            catch (Exception ex)
            {
                AppendLog("Config load", "Could not read " + path, ex);
            }

            config = null;
            return false;
        }
    }
}

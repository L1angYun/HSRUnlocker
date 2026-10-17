using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace StarRailUnlocker.Core
{
    /// <summary>Find installed clients using read-only launcher and uninstall metadata.</summary>
    public static class GameInstallationDiscovery
    {
        private const int MaxHypVersions = 32;
        private const int MaxUninstallEntries = 1024;
        private const int MaxPathLength = 32767;
        private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        public static IReadOnlyList<string> FindInstalled() =>
            FindInstalled(ReadSubKeys, ReadString, File.Exists, MobileLaunchSettings.ReadOptionalLines);

        // All external reads are replaceable so verification never visits installed clients or live registry data.
        internal static IReadOnlyList<string> FindInstalled(
            Func<RegistryHive, RegistryView, string, IEnumerable<string>> readSubKeys,
            Func<RegistryHive, RegistryView, string, string, string> readString,
            Func<string, bool> fileExists, Func<string, string[]> readLines)
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            string Value(RegistryHive hive, RegistryView view, string path, string name)
            {
                try { return readString(hive, view, path, name); }
                catch (Exception) { return null; }
            }

            void AddExecutable(string path)
            {
                string executable = LocalPath(path);
                if (executable == null || !Path.GetFileName(executable).Equals("StarRail.exe", StringComparison.OrdinalIgnoreCase))
                    return;
                try { if (fileExists(executable)) found.Add(executable); }
                catch (Exception) { }
            }

            void AddDirectory(string raw, bool legacy)
            {
                string directory = LocalPath(raw);
                if (directory == null) return;
                if (legacy && Path.GetFileName(directory).Equals("StarRail.exe", StringComparison.OrdinalIgnoreCase))
                {
                    AddExecutable(directory);
                    return;
                }
                AddExecutable(Path.Combine(directory, "StarRail.exe"));
                if (!legacy) return;
                try
                {
                    string[] lines = readLines(Path.Combine(directory, "config.ini"));
                    string installed = LauncherDirectory(lines);
                    if (installed != null) AddExecutable(Path.Combine(installed, "StarRail.exe"));
                }
                catch (Exception) { }
            }

            foreach (string vendor in new[] { "miHoYo", "Cognosphere" })
            {
                string root = @"Software\" + vendor + @"\HYP";
                foreach (string version in Children(readSubKeys, RegistryHive.CurrentUser, RegistryView.Registry64, root, MaxHypVersions))
                {
                    foreach (string business in new[] { "hkrpg_cn", "hkrpg_global" })
                        AddDirectory(Value(RegistryHive.CurrentUser, RegistryView.Registry64,
                            root + "\\" + version + "\\" + business, "GameInstallPath"), legacy: false);
                }
            }

            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    foreach (string entry in Children(readSubKeys, hive, view, UninstallRoot, MaxUninstallEntries))
                    {
                        string path = UninstallRoot + "\\" + entry;
                        if (!RecognizedName(Value(hive, view, path, "DisplayName"))) continue;
                        AddDirectory(Value(hive, view, path, "InstallLocation"), legacy: true);
                        AddDirectory(Value(hive, view, path, "InstallPath"), legacy: true);
                    }
                }
            }
            return found.ToArray();
        }

        private static IReadOnlyList<string> Children(
            Func<RegistryHive, RegistryView, string, IEnumerable<string>> read,
            RegistryHive hive, RegistryView view, string path, int limit)
        {
            var children = new List<string>();
            try
            {
                foreach (string child in read(hive, view, path) ?? Array.Empty<string>())
                {
                    if (limit-- == 0) break;
                    if (!string.IsNullOrWhiteSpace(child) && child.Length <= 255 &&
                        child.IndexOfAny(new[] { '\\', '/', '\0' }) < 0)
                        children.Add(child);
                }
            }
            catch (Exception) { }
            return children;
        }

        private static bool RecognizedName(string name)
        {
            string trimmed = name?.Trim();
            return trimmed != null && new[] { "Star Rail", "Honkai Star Rail", "Honkai: Star Rail",
                "崩坏：星穹铁道", "崩壞：星穹鐵道" }.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
        }

        private static string LauncherDirectory(IReadOnlyList<string> lines)
        {
            if (lines == null) return null;
            int bytes = 0;
            bool launcher = false;
            string directory = null;
            foreach (string raw in lines)
            {
                bytes += Encoding.UTF8.GetByteCount(raw ?? string.Empty) + 2;
                if (bytes > MobileLaunchSettings.MaxConfigMetadataBytes) return null;
                string line = (raw ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    launcher = line.Equals("[launcher]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int separator = line.IndexOf('=');
                if (!launcher || separator < 0 ||
                    !line[..separator].Trim().Equals("game_install_path", StringComparison.OrdinalIgnoreCase))
                    continue;
                string next = LocalPath(line[(separator + 1)..]);
                if (next == null || (directory != null && !directory.Equals(next, StringComparison.OrdinalIgnoreCase)))
                    return null;
                directory = next;
            }
            return directory;
        }

        private static string LocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length >= MaxPathLength) return null;
            string path = value.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path[2..].Contains(':') ||
                path.Any(character => char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
                return null;
            try
            {
                string full = Path.GetFullPath(path);
                return full.Length < MaxPathLength - 14 ? full : null;
            }
            catch (Exception) { return null; }
        }

        private static IEnumerable<string> ReadSubKeys(RegistryHive hive, RegistryView view, string path)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            // Avoid allocating an unbounded registry name array before applying the per-root limit.
            int limit = path.EndsWith(@"\HYP", StringComparison.OrdinalIgnoreCase) ? MaxHypVersions : MaxUninstallEntries;
            return key == null || key.SubKeyCount > limit ? Array.Empty<string>() : key.GetSubKeyNames();
        }

        private static string ReadString(RegistryHive hive, RegistryView view, string path, string name)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
    }
}

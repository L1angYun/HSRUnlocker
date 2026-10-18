using System.Text;
using System.Text.Json;
using StarRailUnlocker.Core;

internal static class ConfigStorageVerification
{
    public static void Run(Action<bool, string> check)
    {
        VerifyLoad(check);
        VerifySave(check);
        VerifyLateReplacementFailure(check);
    }

    private static void VerifyLateReplacementFailure(Action<bool, string> check)
    {
        foreach (bool hadBackup in new[] { true, false })
        {
            using var fixture = new Fixture();
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            if (hadBackup) fixture.Write(fixture.Backup, backup);
            fixture.Write(fixture.UnrelatedTemp, "another attempt");
            var storage = new ConfigStorage(fixture.Primary, fixture.Legacy, fixture.Record,
                (replacement, destination, backupPath) =>
                {
                    File.Move(destination, backupPath, overwrite: true);
                    throw new IOException("Injected ERROR_UNABLE_TO_MOVE_REPLACEMENT_2", unchecked((int)0x80070499));
                });
            check(!storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  HasBytes(fixture.Primary, primary) &&
                  (hadBackup ? HasBytes(fixture.Backup, backup) : !File.Exists(fixture.Backup)),
                $"late replacement failure restores the renamed primary and original backup presence={hadBackup}");
            check(fixture.OnlyExpectedFiles(hadBackup
                    ? new[] { fixture.Primary, fixture.Backup, fixture.UnrelatedTemp }
                    : new[] { fixture.Primary, fixture.UnrelatedTemp }),
                $"reconciled replacement failure cleans only owned staging with original backup presence={hadBackup}");
        }

        foreach (string primary in new[] { "{broken", "null" })
        {
            using var fixture = new Fixture();
            fixture.Write(fixture.Primary, primary);
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Backup, backup);
            var storage = new ConfigStorage(fixture.Primary, fixture.Legacy, fixture.Record,
                (replacement, destination, backupPath) =>
                {
                    if (backupPath != null) throw new InvalidOperationException("Invalid primary must not displace a good backup.");
                    File.Delete(destination);
                    throw new IOException("Injected ERROR_UNABLE_TO_MOVE_REPLACEMENT", unchecked((int)0x80070498));
                });
            check(!storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  HasBytes(fixture.Primary, primary) && HasBytes(fixture.Backup, backup) &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                $"late no-backup replacement failure restores the removed {primary} primary without touching good backup");
        }

        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            var storage = new ConfigStorage(fixture.Primary, fixture.Legacy, fixture.Record,
                (replacement, destination, backupPath) =>
                {
                    File.Move(destination, backupPath, overwrite: true);
                    Directory.CreateDirectory(destination);
                    throw new IOException("Injected late replacement failure followed by blocked rollback");
                });
            check(!storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  fixture.OwnedFiles.Any(path => path.EndsWith(".primary", StringComparison.Ordinal) &&
                      HasBytes(path, primary)) &&
                  HasBytes(fixture.Backup, backup) &&
                  fixture.Diagnostics.Any(entry => entry.Stage == "Config rollback" &&
                      entry.Message.Contains(fixture.Primary, StringComparison.Ordinal)),
                "blocked primary rollback retains its recoverable original, reports its path and still restores backup");
        }

        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            FileStream held = null;
            try
            {
                var storage = new ConfigStorage(fixture.Primary, fixture.Legacy, fixture.Record,
                    (replacement, destination, backupPath) =>
                    {
                        File.Move(destination, backupPath, overwrite: true);
                        held = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        throw new IOException("Injected late replacement failure followed by locked backup rollback");
                    });
                bool saved = storage.Save(new UnlockConfig { TargetFramerate = 240 });
                string recoveryPath = fixture.OwnedFiles.SingleOrDefault(path => path.EndsWith(".backup", StringComparison.Ordinal));
                check(!saved && HasBytes(fixture.Primary, primary) &&
                      recoveryPath != null && HasBytes(recoveryPath, backup) &&
                      fixture.Diagnostics.Any(entry => entry.Stage == "Config rollback" &&
                          entry.Message.Contains(recoveryPath, StringComparison.Ordinal)),
                    "locked backup rollback retains and reports its last recoverable original after primary restoration");
            }
            finally { held?.Dispose(); }
        }

        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            var storage = new ConfigStorage(fixture.Primary, fixture.Legacy, fixture.Record,
                (replacement, destination, backupPath) =>
                {
                    File.Replace(replacement, destination, backupPath);
                    throw new IOException("Injected failure reported after all replacement transitions");
                });
            check(!storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  HasBytes(fixture.Primary, primary) && HasBytes(fixture.Backup, backup) &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                "failure reported after replacement publication atomically restores both byte-exact originals");
        }
    }

    private static bool HasBytes(string path, string text) =>
        File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(text));

    private static void VerifyLoad(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            const string primary = """{"targetframerate":999,"FovDelta":-90,"Language":"ja","GamePaths":null,"SafeAreaMode":999,"UpdateSource":999}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, """{"TargetFramerate":165}""");
            fixture.Write(fixture.Legacy, """{"TargetFramerate":240}""");
            var loaded = fixture.Storage.Load();
            check(loaded.TargetFramerate == 480 && loaded.FovDelta == -60 &&
                  loaded.Language == "ja-JP" && loaded.GamePaths.Length == 0 &&
                  loaded.SafeAreaMode == MobileSafeAreaMode.Original &&
                  loaded.UpdateSource == UpdateSourcePreference.Auto &&
                  loaded.ExitWithGame && loaded.CheckForUpdates,
                "primary settings beat backup and legacy while old JSON is normalized with compatible defaults");
            check(File.ReadAllText(fixture.Primary) == primary && fixture.Diagnostics.Count == 0,
                "loading a valid primary does not rewrite settings or report recovery");
        }

        string originalLanguage = L10n.Preference;
        try
        {
            foreach (var locale in new[] { ("zh-CN", "备份"), ("en-US", "backup"), ("ja-JP", "バックアップ"), ("ko-KR", "백업") })
            foreach (string damaged in new[] { null, "{broken", "null" })
            {
                using var fixture = new Fixture();
                L10n.SetLanguage(locale.Item1);
                if (damaged != null) fixture.Write(fixture.Primary, damaged);
                const string backup = """{"TargetFramerate":165,"FovDelta":90,"Language":"ko"}""";
                fixture.Write(fixture.Backup, backup);
                fixture.Write(fixture.Legacy, """{"TargetFramerate":240}""");
                var loaded = fixture.Storage.Load();
                string description = damaged ?? "missing";
                check(loaded.TargetFramerate == 165 && loaded.FovDelta == 60 && loaded.Language == "ko-KR",
                    $"{locale.Item1} loads and normalizes backup before legacy for {description} primary");
                check(fixture.Diagnostics.Any(entry => entry.Stage == "Config recovery" &&
                          entry.Message.Contains(fixture.Backup, StringComparison.Ordinal) &&
                          entry.Message.Contains(locale.Item2, StringComparison.Ordinal)) &&
                      File.ReadAllText(fixture.Backup) == backup &&
                      (damaged == null ? !File.Exists(fixture.Primary) : File.ReadAllText(fixture.Primary) == damaged),
                    $"{locale.Item1} reports backup recovery without changing primary or backup bytes");
            }
        }
        finally { L10n.SetLanguage(originalLanguage); }

        using (var fixture = new Fixture())
        {
            fixture.Write(fixture.Primary, "null");
            fixture.Write(fixture.Backup, "{broken");
            const string legacy = """{"TargetFramerate":240,"Language":"en","CheckForUpdates":false}""";
            fixture.Write(fixture.Legacy, legacy);
            var loaded = fixture.Storage.Load();
            check(loaded.TargetFramerate == 240 && loaded.Language == "en-US" && !loaded.CheckForUpdates &&
                  fixture.Storage.Load().TargetFramerate == 240 && File.ReadAllText(fixture.Legacy) == legacy,
                "unusable primary and backup import legacy settings into the bound path without modifying legacy");
        }

        using (var fixture = new Fixture())
        {
            fixture.Write(fixture.Primary, "{broken");
            fixture.Write(fixture.Backup, "null");
            fixture.Write(fixture.Legacy, "{broken");
            var loaded = fixture.Storage.Load();
            check(loaded.TargetFramerate == 120 && loaded.Language == "auto" &&
                  loaded.ExitWithGame && loaded.CheckForUpdates &&
                  File.ReadAllText(fixture.Primary) == "{broken" && File.ReadAllText(fixture.Backup) == "null",
                "unusable settings and legacy return defaults without replacing damaged files");
        }
    }

    private static void VerifySave(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            fixture.Write(fixture.UnrelatedTemp, "another attempt");
            using (var held = new FileStream(fixture.Primary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                check(!fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 }),
                    "a primary handle allowing writes but denying deletion rejects a non-atomic save");
                check(File.ReadAllText(fixture.Primary) == primary && File.ReadAllText(fixture.Backup) == backup &&
                      fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup, fixture.UnrelatedTemp) &&
                      fixture.Diagnostics.Any(entry => entry.Stage == "Config save" && entry.Error is IOException),
                    "replacement failure preserves both files, reports diagnostics and removes only its own temporary file");
            }
            check(fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  fixture.Storage.Load().TargetFramerate == 240 && File.ReadAllText(fixture.Backup) == primary,
                "saving succeeds after a transient replacement lock is released and snapshots the previous primary");
        }

        using (var fixture = new Fixture())
        {
            var config = new UnlockConfig { TargetFramerate = 999, Language = "ja", GamePaths = null };
            check(fixture.Storage.Save(config) && fixture.Storage.Load().TargetFramerate == 480 &&
                  config.Language == "ja-JP" && !File.Exists(fixture.Backup) &&
                  fixture.OnlyExpectedFiles(fixture.Primary),
                "first save atomically creates normalized compatible JSON without a spurious backup or leftover temp");
            byte[] original = File.ReadAllBytes(fixture.Primary);
            check(fixture.Storage.Save(new UnlockConfig { TargetFramerate = 165 }) &&
                  fixture.Storage.Load().TargetFramerate == 165 &&
                  File.ReadAllBytes(fixture.Backup).SequenceEqual(original) &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                "successful replacement keeps byte-exact last-known-good settings and cleans its temporary file");
            using var json = JsonDocument.Parse(File.ReadAllBytes(fixture.Primary));
            check(json.RootElement.GetProperty("TargetFramerate").GetInt32() == 165 &&
                  json.RootElement.GetProperty("ExitWithGame").GetBoolean() &&
                  json.RootElement.GetProperty("Language").GetString() == "auto" &&
                  !File.ReadAllBytes(fixture.Primary).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }),
                "atomic settings retain the existing JSON property names, defaults and UTF-8 encoding without BOM");
        }

        foreach (string damaged in new[] { "{broken", "null", null })
        {
            using var fixture = new Fixture();
            if (damaged != null) fixture.Write(fixture.Primary, damaged);
            const string backup = """{"TargetFramerate":165}""";
            fixture.Write(fixture.Backup, backup);
            check(fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  fixture.Storage.Load().TargetFramerate == 240 && File.ReadAllText(fixture.Backup) == backup &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                $"saving after {damaged ?? "missing"} primary preserves a valid backup");
        }

        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            using (var held = new FileStream(fixture.Backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                check(!fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                      File.ReadAllText(fixture.Primary) == primary && File.ReadAllText(fixture.Backup) == backup &&
                      fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                    "a locked backup cannot cause a failed replacement to damage either existing settings file");
            }
        }

        using (var fixture = new Fixture())
        {
            fixture.Write(fixture.Primary, """{"TargetFramerate":165}""");
            using (var held = new FileStream(fixture.Primary, FileMode.Open, FileAccess.Write, FileShare.Delete))
            {
                check(!fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                      !File.Exists(fixture.Backup) && fixture.OnlyExpectedFiles(fixture.Primary),
                    "an unreadable primary is not mistaken for corruption and overwritten without a good backup");
            }
            check(fixture.Storage.Load().TargetFramerate == 165,
                "the unreadable primary remains intact after its handle is released");
        }

        using (var fixture = new Fixture())
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            var blocked = new ConfigStorage(Path.Combine(fixture.Primary, "settings.json"), fixture.Legacy, fixture.Record);
            check(!blocked.Save(new UnlockConfig { TargetFramerate = 240 }) &&
                  File.ReadAllText(fixture.Primary) == primary && File.ReadAllText(fixture.Backup) == backup &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup),
                "a real directory/write failure returns false without changing existing bytes or leaking temporary files");
        }

        using (var fixture = new Fixture(new string('s', 240)))
        {
            const string primary = """{"TargetFramerate":165}""";
            const string backup = """{"TargetFramerate":90}""";
            fixture.Write(fixture.Primary, primary);
            fixture.Write(fixture.Backup, backup);
            bool saved = fixture.Storage.Save(new UnlockConfig { TargetFramerate = 240 });
            check(!saved &&
                  File.ReadAllText(fixture.Primary) == primary && File.ReadAllText(fixture.Backup) == backup &&
                  fixture.OnlyExpectedFiles(fixture.Primary, fixture.Backup) &&
                  fixture.Diagnostics.Any(entry => entry.Stage == "Config save" && entry.Error is IOException),
                "temporary-file creation failure never falls back to truncating a writable primary");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HSRUnlocker-settings-" + Guid.NewGuid().ToString("N")));
        private readonly string fileName;
        internal string Primary => Path.Combine(root, fileName);
        internal string Backup => Primary + ".bak";
        internal string Legacy => Path.Combine(root, "hsr_config.json");
        internal string UnrelatedTemp => Path.Combine(root, "settings.json.unrelated.tmp");
        internal readonly List<(string Stage, string Message, Exception Error)> Diagnostics = new();
        internal ConfigStorage Storage { get; }

        internal Fixture(string fileName = "settings.json")
        {
            this.fileName = fileName;
            Directory.CreateDirectory(root);
            Storage = new ConfigStorage(Primary, Legacy, Record);
        }

        internal void Record(string stage, string message, Exception error) => Diagnostics.Add((stage, message, error));
        internal void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
        internal IEnumerable<string> OwnedFiles => Directory.EnumerateFiles(root);
        internal bool OnlyExpectedFiles(params string[] expected) =>
            Directory.EnumerateFiles(root).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("HSRUnlocker-settings-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected settings fixture cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }
}

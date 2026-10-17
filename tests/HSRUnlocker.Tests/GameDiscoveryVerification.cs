using Microsoft.Win32;
using StarRailUnlocker.Core;

internal static class GameDiscoveryVerification
{
    public static void Run(Action<bool, string> check)
    {
        // Each fixture replaces only OS reads; discovery and path/metadata validation stay real.
        var hyp = new Fixture();
        hyp.Hyp("miHoYo", "1.0", "hkrpg_cn", @"D:\arbitrary folder\CN");
        hyp.Hyp("Cognosphere", "2.0", "hkrpg_global", @"E:\自定义\游戏");
        hyp.Files.UnionWith(new[] { @"D:\arbitrary folder\CN\StarRail.exe", @"E:\自定义\游戏\StarRail.exe" });
        check(hyp.Find().SequenceEqual(new[] { @"D:\arbitrary folder\CN\StarRail.exe", @"E:\自定义\游戏\StarRail.exe" }),
            "HYP both vendors and businesses find existing executables in arbitrary and Unicode directories");

        var cross = new Fixture();
        cross.Hyp("miHoYo", "3", "hkrpg_global", @"D:\global");
        cross.Hyp("Cognosphere", "4", "hkrpg_cn", @"D:\china");
        cross.Files.UnionWith(new[] { @"D:\global\StarRail.exe", @"D:\china\StarRail.exe" });
        check(cross.Find().SequenceEqual(new[] { @"D:\china\StarRail.exe", @"D:\global\StarRail.exe" }),
            "HYP business identity is independent of vendor and directory spelling");

        var stale = new Fixture();
        stale.Hyp("miHoYo", "1", "hkrpg_cn", @"D:\missing");
        stale.Hyp("miHoYo", "1", "hk4e_cn", @"D:\other");
        stale.Files.Add(@"D:\other\StarRail.exe");
        check(stale.Find().Count == 0, "stale HYP paths and other game businesses are ignored");

        var duplicates = new Fixture();
        duplicates.Hyp("miHoYo", "1", "hkrpg_cn", @"D:\Game\.");
        duplicates.Hyp("Cognosphere", "2", "hkrpg_global", @"d:\game");
        duplicates.Files.Add(@"D:\Game\StarRail.exe");
        check(duplicates.Find().Count == 1, "HYP paths normalize and deduplicate without case sensitivity");

        foreach (string invalid in new[] { @"\\server\game", @"\\?\D:\game", "https://example/game", @"D:game",
                     @"\game", @"game", @"D:\game:stream", @"D:\bad|name", "D:\\" + new string('x', 32768) })
        {
            var fixture = new Fixture();
            fixture.Hyp("miHoYo", "1", "hkrpg_cn", invalid);
            fixture.ExistsByDefault = true;
            check(fixture.Find().Count == 0, "discovery rejects nonlocal, malformed or oversized path: " +
                (invalid.Length < 80 ? invalid : "oversized"));
        }

        var uninstall = new Fixture();
        uninstall.Uninstall(RegistryHive.CurrentUser, RegistryView.Registry64, "one", "Honkai: Star Rail", @"D:\custom");
        uninstall.Uninstall(RegistryHive.LocalMachine, RegistryView.Registry32, "two", "崩坏：星穹铁道", @"E:\中文");
        uninstall.Uninstall(RegistryHive.LocalMachine, RegistryView.Registry64, "three", "崩壞：星穹鐵道", @"F:\繁體");
        uninstall.Uninstall(RegistryHive.CurrentUser, RegistryView.Registry32, "four", "Star Rail", @"G:\short");
        uninstall.Files.UnionWith(new[] { @"D:\custom\StarRail.exe", @"E:\中文\StarRail.exe", @"F:\繁體\StarRail.exe", @"G:\short\StarRail.exe" });
        check(uninstall.Find().Count == 4, "recognized uninstall names discover HKCU and both HKLM views");

        var unrelated = new Fixture();
        unrelated.Uninstall(RegistryHive.CurrentUser, RegistryView.Registry64, "bad", "Star Rail wallpaper", @"D:\fake");
        unrelated.Files.Add(@"D:\fake\StarRail.exe");
        check(unrelated.Find().Count == 0, "unrelated uninstall display names cannot nominate executables");

        var legacy = new Fixture();
        legacy.Uninstall(RegistryHive.LocalMachine, RegistryView.Registry32, "legacy", "Honkai Star Rail", @"D:\launcher");
        legacy.Lines[@"D:\launcher\config.ini"] = new[] { "[General]", @"game_install_path=D:\wrong", "[launcher]", @"game_install_path = E:/custom/game" };
        legacy.Files.UnionWith(new[] { @"D:\wrong\StarRail.exe", @"E:\custom\game\StarRail.exe" });
        check(legacy.Find().SequenceEqual(new[] { @"E:\custom\game\StarRail.exe" }),
            "legacy launcher config reads game_install_path only inside launcher section");
        legacy.Lines[@"D:\launcher\config.ini"] = new[] { "[launcher]", @"game_install_path=E:\custom\game", @"game_install_path=E:\conflict" };
        check(legacy.Find().Count == 0, "conflicting launcher paths are refused");
        legacy.Lines[@"D:\launcher\config.ini"] = new[] { "[launcher]", "game_install_path=" + new string('x', 65536) };
        check(legacy.Find().Count == 0, "oversized launcher metadata is refused before path probing");

        var access = new Fixture();
        access.Hyp("miHoYo", "bad", "hkrpg_cn", @"D:\denied");
        access.Hyp("miHoYo", "file-denied", "hkrpg_cn", @"D:\file-denied");
        access.Hyp("Cognosphere", "good", "hkrpg_global", @"E:\working");
        access.Files.Add(@"E:\working\StarRail.exe");
        access.DeniedFile = @"D:\file-denied\StarRail.exe";
        access.DeniedRegistry = @"Software\miHoYo\HYP\bad";
        access.Uninstall(RegistryHive.CurrentUser, RegistryView.Registry64, "unreadable", "Star Rail", @"D:\unreadable");
        access.Lines[@"D:\unreadable\config.ini"] = null;
        check(access.Find().SequenceEqual(new[] { @"E:\working\StarRail.exe" }),
            "individual registry, file and metadata access errors do not hide other installations");

        var bounded = new Fixture();
        string hypRoot = @"Software\miHoYo\HYP";
        bounded.Children[Fixture.Key(RegistryHive.CurrentUser, RegistryView.Registry64, hypRoot)] =
            Enumerable.Range(0, 10000).Select(index => index.ToString()).ToArray();
        bounded.Hyp("miHoYo", "9999", "hkrpg_cn", @"D:\late");
        bounded.Files.Add(@"D:\late\StarRail.exe");
        bounded.Children[Fixture.Key(RegistryHive.LocalMachine, RegistryView.Registry64, Fixture.UninstallRoot)] =
            Enumerable.Range(0, 10000).Select(index => index.ToString()).ToArray();
        bounded.Uninstall(RegistryHive.LocalMachine, RegistryView.Registry64, "9999", "Star Rail", @"D:\late");
        check(bounded.Find().Count == 0 && bounded.RegistryReads < 3000,
            "registry enumeration and candidate reads are bounded for oversized roots");
    }

    private sealed class Fixture
    {
        internal const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        internal readonly Dictionary<string, string[]> Children = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string[]> Lines = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        internal bool ExistsByDefault;
        internal string DeniedRegistry, DeniedFile;
        internal int RegistryReads;

        internal static string Key(RegistryHive hive, RegistryView view, string path) => $"{hive}|{view}|{path}";

        internal void Hyp(string vendor, string version, string business, string directory)
        {
            string root = @"Software\" + vendor + @"\HYP";
            AddChild(RegistryHive.CurrentUser, RegistryView.Registry64, root, version);
            Values[Key(RegistryHive.CurrentUser, RegistryView.Registry64, root + "\\" + version + "\\" + business) + "|GameInstallPath"] = directory;
        }

        internal void Uninstall(RegistryHive hive, RegistryView view, string entry, string name, string directory)
        {
            AddChild(hive, view, UninstallRoot, entry);
            string key = Key(hive, view, UninstallRoot + "\\" + entry);
            Values[key + "|DisplayName"] = name;
            Values[key + "|InstallLocation"] = directory;
        }

        private void AddChild(RegistryHive hive, RegistryView view, string path, string name)
        {
            string key = Key(hive, view, path);
            if (!Children.TryGetValue(key, out var children)) children = Array.Empty<string>();
            if (!children.Contains(name)) Children[key] = children.Append(name).ToArray();
        }

        internal IReadOnlyList<string> Find() => GameInstallationDiscovery.FindInstalled(
            (hive, view, path) => Children.GetValueOrDefault(Key(hive, view, path)) ?? Array.Empty<string>(),
            (hive, view, path, name) =>
            {
                RegistryReads++;
                if (DeniedRegistry != null && path.StartsWith(DeniedRegistry, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException();
                return Values.GetValueOrDefault(Key(hive, view, path) + "|" + name);
            },
            path => path == DeniedFile ? throw new IOException() : ExistsByDefault || Files.Contains(path),
            path => Lines.TryGetValue(path, out var lines) && lines == null ? throw new IOException() : lines);
    }
}

using System.Text;
using System.Text.Json;
using StarRailUnlocker.Core;

internal static class MobileLaunchVerification
{
    private static readonly string FixtureExecutable = Path.Combine(Path.GetTempPath(), "HSRUnlocker-mobile-fixture", "StarRail.exe");
    private static readonly string[] ChinaApp = { "miHoYo", "崩坏：星穹铁道" };
    private static readonly string[] GlobalApp = { "Cognosphere", "Star Rail" };
    // Includes binary values and two trailing NUL bytes. No text conversion or trimming is valid.
    private static readonly byte[] Graphics = { 0, 1, 254, 255, 0, 0 };
    private const string ExpectedBase64 = "AAH+/wAA";

    public static void Run(Action<bool, string> check, string[] args)
    {
        VerifySelection(check, "missing config.ini falls back to recognized CN app.info", MobileGraphicsSource.Auto,
            null, ChinaApp, MobileLaunchSettings.ChinaRegistryPath);
        VerifySelection(check, "CN uapc selects exactly the CN graphics source", MobileGraphicsSource.Auto,
            new[] { "[launcher]", " uapc = hkrpg_cn " }, null, MobileLaunchSettings.ChinaRegistryPath);
        VerifySelection(check, "Global uapc selects exactly the Global graphics source", MobileGraphicsSource.Auto,
            new[] { "uapc=hkrpg_global" }, null, MobileLaunchSettings.GlobalRegistryPath);
        VerifySelection(check, "JSON CN uapc agrees with CN app.info", MobileGraphicsSource.Auto,
            new[] { "uapc={\"hkrpg_cn\":{\"channel_id\":1}}" }, ChinaApp, MobileLaunchSettings.ChinaRegistryPath);
        VerifySelection(check, "JSON Global uapc agrees with Global app.info", MobileGraphicsSource.Auto,
            new[] { "uapc={\"hkrpg_global\":{\"channel_id\":1}}" }, GlobalApp, MobileLaunchSettings.GlobalRegistryPath);
        VerifySelection(check, "missing config.ini falls back to recognized Global app.info", MobileGraphicsSource.Auto,
            null, GlobalApp, MobileLaunchSettings.GlobalRegistryPath);
        VerifySelection(check, "explicit CN works without consulting metadata", MobileGraphicsSource.China,
            null, null, MobileLaunchSettings.ChinaRegistryPath);
        VerifySelection(check, "explicit Global works without consulting metadata", MobileGraphicsSource.Global,
            null, null, MobileLaunchSettings.GlobalRegistryPath);
        VerifySelection(check, "explicit source overrides conflicting metadata without reading it", MobileGraphicsSource.Global,
            new[] { "uapc=hkrpg_cn" }, ChinaApp, MobileLaunchSettings.GlobalRegistryPath);

        VerifyRefusal(check, "missing identities are refused before registry reads", null, null);
        VerifyRefusal(check, "metadata without uapc is refused before registry reads", new[] { "[launcher]", "game_version=1" }, null);
        VerifyRefusal(check, "unknown uapc is refused even with recognized app.info", new[] { "uapc=unknown" }, ChinaApp);
        VerifyRefusal(check, "malformed uapc JSON is refused before registry reads", new[] { "uapc={\"hkrpg_global\":" }, null);
        VerifyRefusal(check, "unknown app.info is refused even with recognized uapc", new[] { "uapc=hkrpg_cn" }, new[] { "Unknown", "Star Rail" });
        VerifyRefusal(check, "CN config and Global app.info conflict before registry reads", new[] { "uapc=hkrpg_cn" }, GlobalApp);
        VerifyRefusal(check, "Global config and CN app.info conflict before registry reads", new[] { "uapc=hkrpg_global" }, ChinaApp);
        VerifyRefusal(check, "conflicting duplicate uapc fields are refused before registry reads",
            new[] { "uapc=hkrpg_cn", "uapc=hkrpg_global" }, null);
        VerifyRefusal(check, "both channels in uapc JSON are refused before registry reads",
            new[] { "uapc={\"hkrpg_cn\":{},\"hkrpg_global\":{}}" }, null);
        VerifyRefusal(check, "extra app.info identity lines are refused before registry reads", null,
            new[] { "miHoYo", "崩坏：星穹铁道", "unrecognized" });
        VerifyRefusal(check, "invalid source enum is refused before metadata or registry reads", null, null,
            (MobileGraphicsSource)999);

        VerifyGraphicsRefusal(check, null, "missing graphics value is refused without fallback reads");
        VerifyGraphicsRefusal(check, Array.Empty<byte>(), "empty graphics bytes are refused without fallback reads");
        VerifyGraphicsRefusal(check, "{\"preset\":\"high\"}", "wrong-type graphics string is refused without fallback reads");
        VerifyGraphicsRefusal(check, 120, "wrong-type graphics integer is refused without fallback reads");
        VerifyConfig(check);
        VerifyBoundedFiles(check);

        int appInfoIndex = Array.IndexOf(args, "--mobile-app-info");
        if (appInfoIndex >= 0)
        {
            if (appInfoIndex + 1 >= args.Length) throw new ArgumentException("--mobile-app-info requires an app.info path");
            string path = Path.GetFullPath(args[appInfoIndex + 1]);
            if (!Path.GetFileName(path).Equals("app.info", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--mobile-app-info accepts only an app.info metadata file");
            string[] lines = MobileLaunchSettings.ReadOptionalLines(path);
            check(lines != null, "optional installed app.info is present and was read within the byte limit");
            MobileGraphicsSource source = MobileLaunchSettings.ResolveSource(MobileGraphicsSource.Auto, null, lines);
            check(source != MobileGraphicsSource.Auto, "optional installed app.info identifies " + source + " without game launch or registry access");
        }
    }

    private static void VerifySelection(Action<bool, string> check, string description, MobileGraphicsSource source,
        string[] ini, string[] app, string expectedRegistry)
    {
        int metadataReads = 0;
        var registryReads = new List<(string Path, string Name)>();
        var logs = new List<string>();
        bool correctMetadataPaths = true;
        byte[] original = (byte[])Graphics.Clone();
        IReadOnlyList<string> arguments = MobileLaunchSettings.BuildArguments(FixtureExecutable, source, path =>
        {
            metadataReads++;
            if (path == Path.Combine(Path.GetDirectoryName(FixtureExecutable), "config.ini")) return ini;
            if (path == Path.Combine(Path.GetDirectoryName(FixtureExecutable), "StarRail_Data", "app.info")) return app;
            correctMetadataPaths = false;
            return null;
        }, (path, name) =>
        {
            registryReads.Add((path, name));
            return Graphics;
        }, logs.Add);
        check(correctMetadataPaths && metadataReads == (source == MobileGraphicsSource.Auto ? 2 : 0)
            && registryReads.Count == 1 && registryReads[0] == (expectedRegistry, MobileLaunchSettings.GraphicsValueName)
            && logs.Any(line => line.Contains("只读 HKCU\\" + expectedRegistry, StringComparison.Ordinal)), description);
        check(arguments.SequenceEqual(new[] { "-platform_type", "CLOUD_WEB_TOUCH", "-graphics_setting", ExpectedBase64 })
            && Convert.FromBase64String(arguments[3]).SequenceEqual(original) && Graphics.SequenceEqual(original),
            description + "; only the original two argument pairs preserve every byte and trailing NUL");
    }

    private static void VerifyRefusal(Action<bool, string> check, string description, string[] ini, string[] app,
        MobileGraphicsSource source = MobileGraphicsSource.Auto)
    {
        int registryReads = 0, metadataReads = 0;
        bool refused = false;
        try
        {
            MobileLaunchSettings.BuildArguments(FixtureExecutable, source, path =>
            {
                metadataReads++;
                return Path.GetFileName(path) == "app.info" ? app : ini;
            }, (_, _) => { registryReads++; return Graphics; });
        }
        catch (InvalidOperationException ex) { refused = !string.IsNullOrWhiteSpace(ex.Message); }
        check(refused && registryReads == 0 && (Enum.IsDefined(source) || metadataReads == 0), description);
    }

    private static void VerifyGraphicsRefusal(Action<bool, string> check, object value, string description)
    {
        int registryReads = 0;
        bool refused = false;
        bool correctSelection = true;
        try
        {
            MobileLaunchSettings.BuildArguments(FixtureExecutable, MobileGraphicsSource.Global,
                _ => throw new InvalidOperationException("Manual source must not read metadata"), (path, name) =>
                {
                    registryReads++;
                    correctSelection &= path == MobileLaunchSettings.GlobalRegistryPath && name == MobileLaunchSettings.GraphicsValueName;
                    return value;
                });
        }
        catch (InvalidOperationException ex) { refused = ex.Message.Contains("图形设置", StringComparison.Ordinal); }
        check(refused && correctSelection && registryReads == 1, description);
    }

    private static void VerifyConfig(Action<bool, string> check)
    {
        const string legacyJson = "{\"ProcessPath\":\"client\\\\StarRail.exe\",\"TargetFramerate\":144,\"FovDelta\":12.5,\"AutoStart\":true,\"StartMinimized\":true,\"UseMobileUI\":true}";
        var legacy = JsonSerializer.Deserialize<UnlockConfig>(legacyJson);
        check(legacy != null && legacy.GraphicsSource == MobileGraphicsSource.Auto && legacy.UseMobileUI
            && legacy.TargetFramerate == 144 && legacy.FovDelta == 12.5 && legacy.AutoStart && legacy.StartMinimized,
            "legacy six-field JSON defaults GraphicsSource to Auto and preserves its fields without settings I/O");
        var invalid = JsonSerializer.Deserialize<UnlockConfig>("{\"GraphicsSource\":999}");
        invalid.Normalize();
        check(invalid.GraphicsSource == MobileGraphicsSource.Auto, "invalid enum JSON normalizes to Auto");
        legacy.GraphicsSource = MobileGraphicsSource.Global;
        UnlockConfig clone = legacy.Clone();
        check(!ReferenceEquals(legacy, clone) && clone.GraphicsSource == MobileGraphicsSource.Global
            && clone.ProcessPath == legacy.ProcessPath && clone.TargetFramerate == legacy.TargetFramerate
            && clone.FovDelta == legacy.FovDelta && clone.AutoStart == legacy.AutoStart
            && clone.StartMinimized == legacy.StartMinimized && clone.UseMobileUI == legacy.UseMobileUI,
            "Clone preserves GraphicsSource and all six existing settings");
        string json = JsonSerializer.Serialize(clone);
        using var document = JsonDocument.Parse(json);
        UnlockConfig roundTrip = JsonSerializer.Deserialize<UnlockConfig>(json);
        roundTrip.Normalize();
        check(document.RootElement.GetProperty("GraphicsSource").GetInt32() == (int)MobileGraphicsSource.Global
            && roundTrip.GraphicsSource == clone.GraphicsSource && roundTrip.ProcessPath == clone.ProcessPath
            && roundTrip.TargetFramerate == clone.TargetFramerate && roundTrip.UseMobileUI == clone.UseMobileUI,
            "selected source serializes and round-trips without loading or saving live settings");
        foreach (MobileGraphicsSource source in Enum.GetValues<MobileGraphicsSource>())
        {
            var config = new UnlockConfig { GraphicsSource = source };
            config.Normalize();
            check(config.GraphicsSource == source && JsonSerializer.Deserialize<UnlockConfig>(JsonSerializer.Serialize(config)).GraphicsSource == source,
                source + " source survives normalization and JSON round-trip");
        }
    }

    private static void VerifyBoundedFiles(Action<bool, string> check)
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HSRUnlocker-mobile-verification-" + Guid.NewGuid().ToString("N")));
        string dataDirectory = Path.Combine(directory, "StarRail_Data");
        string iniPath = Path.Combine(directory, "config.ini"), appPath = Path.Combine(dataDirectory, "app.info");
        string executable = Path.Combine(directory, "StarRail.exe");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            check(MobileLaunchSettings.ReadOptionalLines(iniPath) == null && MobileLaunchSettings.ReadOptionalLines(appPath) == null,
                "missing metadata files remain optional without registry access");
            File.WriteAllText(appPath, "miHoYo\r\n崩坏：星穹铁道\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            int reads = 0;
            IReadOnlyList<string> args = MobileLaunchSettings.BuildArguments(executable, MobileGraphicsSource.Auto,
                MobileLaunchSettings.ReadOptionalLines, (path, name) =>
                {
                    reads++;
                    if (path != MobileLaunchSettings.ChinaRegistryPath || name != MobileLaunchSettings.GraphicsValueName)
                        throw new InvalidOperationException("Unexpected graphics selection");
                    return Graphics;
                });
            check(reads == 1 && args[3] == ExpectedBase64,
                "bounded UTF-8 BOM app.info file selects CN when config.ini is absent");
            File.WriteAllText(iniPath, "[launcher]\r\nuapc={\"hkrpg_cn\":{}}\r\n", Encoding.Unicode);
            check(MobileLaunchSettings.ResolveSource(MobileGraphicsSource.Auto,
                MobileLaunchSettings.ReadOptionalLines(iniPath), MobileLaunchSettings.ReadOptionalLines(appPath)) == MobileGraphicsSource.China,
                "bounded metadata reader retains BOM-based encoding detection for config.ini");
            File.WriteAllBytes(iniPath, Enumerable.Repeat((byte)' ', MobileLaunchSettings.MaxConfigMetadataBytes).ToArray());
            check(MobileLaunchSettings.ReadOptionalLines(iniPath).Single().Length == MobileLaunchSettings.MaxConfigMetadataBytes,
                "config.ini accepts the exact 64 KiB metadata limit");
            File.WriteAllBytes(iniPath, new byte[MobileLaunchSettings.MaxConfigMetadataBytes + 1]);
            VerifyOversizeRefusal(check, executable, "oversized config.ini is refused before registry reads");
            File.Delete(iniPath); // Exact, tool-created fixture path under the unique temporary directory.
            byte[] identity = Encoding.UTF8.GetBytes("miHoYo\n崩坏：星穹铁道\n");
            byte[] exactApp = Enumerable.Repeat((byte)' ', MobileLaunchSettings.MaxAppInfoBytes).ToArray();
            identity.CopyTo(exactApp, 0);
            File.WriteAllBytes(appPath, exactApp);
            check(MobileLaunchSettings.ResolveSource(MobileGraphicsSource.Auto, null,
                MobileLaunchSettings.ReadOptionalLines(appPath)) == MobileGraphicsSource.China,
                "app.info accepts and parses the exact 4 KiB metadata limit");
            File.WriteAllBytes(appPath, new byte[MobileLaunchSettings.MaxAppInfoBytes + 1]);
            VerifyOversizeRefusal(check, executable, "oversized app.info is refused before registry reads");
            reads = 0;
            args = MobileLaunchSettings.BuildArguments(executable, MobileGraphicsSource.China,
                _ => throw new InvalidOperationException("Explicit choice must bypass even oversized metadata"), (_, _) => { reads++; return Graphics; });
            check(reads == 1 && args[3] == ExpectedBase64, "explicit source bypasses oversized metadata without reading it");
        }
        finally
        {
            // No recursive deletion: remove only the two known fixture files and empty directories.
            if (!Path.GetFullPath(iniPath).Equals(Path.Combine(directory, "config.ini"), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(appPath).Equals(Path.Combine(directory, "StarRail_Data", "app.info"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fixture cleanup path");
            File.Delete(iniPath);
            File.Delete(appPath);
            Directory.Delete(dataDirectory);
            Directory.Delete(directory);
        }
    }

    private static void VerifyOversizeRefusal(Action<bool, string> check, string executable, string description)
    {
        int reads = 0;
        bool refused = false;
        try
        {
            MobileLaunchSettings.BuildArguments(executable, MobileGraphicsSource.Auto,
                MobileLaunchSettings.ReadOptionalLines, (_, _) => { reads++; return Graphics; });
        }
        catch (InvalidOperationException ex) { refused = ex.Message.Contains("元数据超过", StringComparison.Ordinal); }
        check(refused && reads == 0, description);
    }
}

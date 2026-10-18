using System.Text.Json;
using System.Text.Json.Serialization;
using StarRailUnlocker.Core;

internal static class PreferencesVerification
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<UnlockConfig>(
            """{"TargetFramerate":165,"FovDelta":8.5,"UseMobileUI":true,"Language":"ja-JP"}""");
        legacy.Normalize();
        using var defaults = JsonDocument.Parse(JsonSerializer.Serialize(legacy));
        check(IsBoolean(defaults, "ExitWithGame", true),
            "old settings keep exit-with-game enabled when the preference is absent");
        check(IsBoolean(defaults, "CheckForUpdates", true),
            "old settings enable startup release checks without changing other preferences");
        check(legacy.TargetFramerate == 165 && legacy.FovDelta == 8.5 &&
              legacy.UseMobileUI && legacy.Language == "ja-JP",
            "new preferences preserve legacy game and language settings");

        foreach (bool enabled in new[] { false, true })
        {
            string json = $$"""{"ExitWithGame":{{enabled.ToString().ToLowerInvariant()}},"CheckForUpdates":{{enabled.ToString().ToLowerInvariant()}},"TargetFramerate":240,"Language":"ko-KR"}""";
            var loaded = JsonSerializer.Deserialize<UnlockConfig>(json);
            loaded.Normalize();
            var clone = loaded.Clone();
            var restored = JsonSerializer.Deserialize<UnlockConfig>(JsonSerializer.Serialize(clone));
            using var saved = JsonDocument.Parse(JsonSerializer.Serialize(restored));
            check(IsBoolean(saved, "ExitWithGame", enabled) && IsBoolean(saved, "CheckForUpdates", enabled),
                $"enabled={enabled} exit and update preferences survive JSON, normalization and session snapshots");
            check(restored.TargetFramerate == 240 && restored.Language == "ko-KR",
                $"enabled={enabled} preference persistence leaves existing choices intact");
        }

        VerifySafeArea(check);
    }

    private static void VerifySafeArea(Action<bool, string> check)
    {
        const string legacyJson = """{"TargetFramerate":165,"FovDelta":8.5,"UseMobileUI":true,"GraphicsSource":2,"Language":"ja-JP"}""";
        var legacy = JsonSerializer.Deserialize<UnlockConfig>(legacyJson);
        legacy.Normalize();
        using var defaults = JsonDocument.Parse(JsonSerializer.Serialize(legacy));
        check(IsNumber(defaults, "SafeAreaMode", 0) &&
              IsNumber(defaults, "SafeAreaHorizontalPercent", 5),
            "legacy mobile settings keep the original safe area with inactive adjustable margin defaults");
        check(legacy.UseMobileUI && legacy.GraphicsSource == MobileGraphicsSource.Global &&
              legacy.TargetFramerate == 165 && legacy.FovDelta == 8.5 && legacy.Language == "ja-JP",
            "safe-area defaults leave legacy mobile, graphics, FPS, FOV and language choices unchanged");

        foreach (var sample in new (string Json, int Mode, double Horizontal, string Description)[]
        {
            ("""{"SafeAreaMode":1}""", 1, 5, "selecting custom uses adjustable starting margins"),
            ("""{"SafeAreaMode":1,"SafeAreaHorizontalPercent":8.25}""",
                1, 8.25, "custom fractional percentages survive persistence"),
            ("""{"SafeAreaMode":0,"SafeAreaHorizontalPercent":7.5}""",
                0, 7.5, "original mode preserves remembered custom percentages without enabling them"),
            ("""{"SafeAreaMode":999,"SafeAreaHorizontalPercent":8}""",
                0, 8, "unknown mode falls back to original without losing valid percentages"),
            ("""{"SafeAreaMode":-1,"SafeAreaHorizontalPercent":-2}""",
                0, 0, "negative mode and percentage normalize safely"),
            ("""{"SafeAreaMode":1,"SafeAreaHorizontalPercent":20}""",
                1, 15, "custom margins cannot exceed fifteen percent"),
            ("""{"SafeAreaMode":1,"SafeAreaHorizontalPercent":0}""",
                1, 0, "zero remains a selectable custom percentage"),
            ("""{"SafeAreaMode":1,"SafeAreaHorizontalPercent":15}""",
                1, 15, "fifteen remains a selectable custom percentage")
        })
        {
            var loaded = JsonSerializer.Deserialize<UnlockConfig>(sample.Json);
            loaded.Normalize();
            UnlockConfig clone = loaded.Clone();
            var restored = JsonSerializer.Deserialize<UnlockConfig>(JsonSerializer.Serialize(clone));
            restored.Normalize();
            using var saved = JsonDocument.Parse(JsonSerializer.Serialize(restored));
            check(!ReferenceEquals(loaded, clone) && IsNumber(saved, "SafeAreaMode", sample.Mode) &&
                  IsNumber(saved, "SafeAreaHorizontalPercent", sample.Horizontal), sample.Description);
        }

        var namedNumbers = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity" })
        {
            string json = $$"""{"SafeAreaMode":1,"SafeAreaHorizontalPercent":"{{value}}"}""";
            var loaded = JsonSerializer.Deserialize<UnlockConfig>(json, namedNumbers);
            loaded.Normalize();
            using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Clone()));
            check(IsNumber(normalized, "SafeAreaMode", 1) &&
                  IsNumber(normalized, "SafeAreaHorizontalPercent", 5),
                $"{value} margins normalize to finite defaults before serialization or launch consumption");
        }
    }

    private static bool IsBoolean(JsonDocument document, string name, bool expected) =>
        document.RootElement.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean() == expected;

    private static bool IsNumber(JsonDocument document, string name, double expected) =>
        document.RootElement.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double actual) && actual == expected;
}

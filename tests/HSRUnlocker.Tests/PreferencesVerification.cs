using System.Text.Json;
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
    }

    private static bool IsBoolean(JsonDocument document, string name, bool expected) =>
        document.RootElement.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean() == expected;
}

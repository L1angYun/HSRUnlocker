using System.Text.Json;
using StarRailUnlocker.Core;

internal static class UpdatePreferenceVerification
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<UnlockConfig>(
            """{"TargetFramerate":165,"CheckForUpdates":false,"Language":"ja-JP"}""");
        legacy.Normalize();
        check(HasSource(legacy, 0),
            "older settings without an update source default to Auto after normalization");
        check(HasSource(legacy.Clone(), 0) && legacy.TargetFramerate == 165 &&
              !legacy.CheckForUpdates && legacy.Language == "ja-JP",
            "the legacy update-source default survives cloning without changing existing preferences");

        foreach (int source in new[] { 0, 1, 2 })
        {
            var original = JsonSerializer.Deserialize<UnlockConfig>(
                $$"""{"UpdateSource":{{source}},"TargetFramerate":240,"Language":"ko-KR"}""");
            original.Normalize();
            check(HasSource(original, source),
                $"update source {source} survives JSON loading and normalization");
            var restored = JsonSerializer.Deserialize<UnlockConfig>(JsonSerializer.Serialize(original));
            restored.Normalize();
            check(HasSource(restored, source),
                $"update source {source} survives a serialized JSON round trip");
            var clone = restored.Clone();
            check(!ReferenceEquals(restored, clone) && HasSource(clone, source) &&
                  clone.TargetFramerate == 240 && clone.Language == "ko-KR",
                $"update source {source} survives independent cloning without changing other preferences");
        }

        foreach (int source in new[] { -1, int.MinValue, 3, 999, int.MaxValue })
        {
            var invalid = JsonSerializer.Deserialize<UnlockConfig>(
                $$"""{"UpdateSource":{{source}}}""");
            invalid.Normalize();
            check(HasSource(invalid, 0) && HasSource(invalid.Clone(), 0),
                $"undefined update source {source} normalizes to Auto before serialization and cloning");
        }

        var selected = JsonSerializer.Deserialize<UnlockConfig>("""{"UpdateSource":2}""");
        selected.Normalize();
        var snapshot = selected.Clone();
        // Reflection keeps this fixture runnable before the new config member exists.
        typeof(UnlockConfig).GetProperty("UpdateSource")?.SetValue(selected, UpdateSourcePreference.GitHub);
        check(HasSource(selected, 1) && HasSource(snapshot, 2),
            "changing the original update source does not mutate the cloned Gitee choice");
        typeof(UnlockConfig).GetProperty("UpdateSource")?.SetValue(snapshot, UpdateSourcePreference.Auto);
        check(HasSource(selected, 1) && HasSource(snapshot, 0),
            "changing the cloned update source does not mutate the original GitHub choice");

        VerifyLocalization(check);
    }

    private static void VerifyLocalization(Action<bool, string> check)
    {
        string originalPreference = L10n.Preference;
        try
        {
            foreach (var sample in new (string Language, string Auto)[]
            {
                ("zh-CN", "自动"), ("en-US", "Auto"),
                ("ja-JP", "自動"), ("ko-KR", "자동")
            })
            {
                L10n.SetLanguage(sample.Language);
                check(L10n.T("Updates.SourceAuto") == sample.Auto &&
                      L10n.T("Updates.SourceGitHub") == "GitHub" &&
                      L10n.T("Updates.SourceGitee") == "Gitee" &&
                      !string.IsNullOrWhiteSpace(L10n.T("Updates.Source")) &&
                      L10n.T("Updates.Source") != "Updates.Source" &&
                      !string.IsNullOrWhiteSpace(L10n.T("Updates.SourceTip")) &&
                      L10n.T("Updates.SourceTip") != "Updates.SourceTip",
                    $"update-source choices and accessible labels resolve in {sample.Language}");
            }
        }
        finally
        {
            L10n.SetLanguage(originalPreference);
        }
    }

    private static bool HasSource(UnlockConfig config, int expected)
    {
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(config));
        return saved.RootElement.TryGetProperty("UpdateSource", out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int actual) && actual == expected;
    }
}

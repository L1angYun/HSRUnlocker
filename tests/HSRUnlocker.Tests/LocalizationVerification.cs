using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StarRailUnlocker.Core;

internal static class LocalizationVerification
{
    public static void Run(Action<bool, string> check)
    {
        CultureInfo originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo originalNumberCulture = CultureInfo.CurrentCulture;
        string originalPreference = L10n.Preference;
        try
        {
            foreach (var sample in new (string Input, string Want)[]
            {
                (Input: (string)null, Want: "auto"), ("", "auto"), (" AUTO ", "auto"),
                ("zh-Hans", "zh-CN"), ("zh-TW", "zh-CN"), ("en_GB", "en-US"),
                ("ja", "ja-JP"), ("KO-kr", "ko-KR"), ("fr-FR", "auto"), ("invalid", "auto"),
                ("en!!!", "auto"), ("zh--CN", "auto")
            })
                check(L10n.NormalizePreference(sample.Input) == sample.Want,
                    $"language preference {sample.Input ?? "(null)"} normalizes to a supported choice");

            foreach (var sample in new (string System, string Want)[]
            {
                (System: "zh-SG", Want: "zh-CN"), ("en-GB", "en-US"),
                ("ja-JP", "ja-JP"), ("ko-KR", "ko-KR"), ("fr-FR", "en-US")
            })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(sample.System);
                L10n.SetLanguage("auto");
                check(L10n.Preference == "auto" && L10n.Language == sample.Want,
                    $"automatic language selection follows {sample.System} with an English fallback");
            }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            L10n.SetLanguage("en-US");
            check(L10n.Language == "en-US" && CultureInfo.CurrentUICulture.Name == "ja-JP",
                "an explicit app language overrides auto detection without changing the caller's culture");

            string[] languages = { "zh-CN", "en-US", "ja-JP", "ko-KR" };
            string[] titles = { "就绪", "Ready", "準備完了", "준비 완료" };
            for (int i = 0; i < languages.Length; i++)
            {
                L10n.SetLanguage(languages[i]);
                check(L10n.T("Ui.StateReady") == titles[i],
                    $"visible ready status uses the selected {languages[i]} language");
                string[] safeAreaKeys =
                {
                    "Ui.SafeAreaMode", "Ui.SafeAreaOriginal", "Ui.SafeAreaCustom",
                    "Ui.SafeAreaHorizontal", "Ui.SafeAreaModeTip", "Ui.SafeAreaHorizontalTip"
                };
                check(safeAreaKeys.All(key => !string.IsNullOrWhiteSpace(L10n.T(key)) && L10n.T(key) != key),
                    $"safe-area choices, numeric labels and accessible explanations resolve in {languages[i]}");
            }
            L10n.SetLanguage("en-US");
            check(L10n.T("Ui.StatusActive", 144, 1234).Contains("144")
                && L10n.T("Ui.StatusActive", 144, 1234).Contains("1234"),
                "translated status keeps both the applied FPS and process identity");
            check(L10n.T("Missing.Public.Key") == "Missing.Public.Key",
                "a missing translation keeps its diagnostic key visible");
            check(L10n.T("Ui.StateReady", 123) == "Ready",
                "unused format values do not alter a plain translated label");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string fov = L10n.T("Runtime.Session.FovEffective", 60.5, 70.25, 70.25, 9.75, 100);
            check(fov.Contains("60.5", StringComparison.Ordinal) && fov.Contains("70.25", StringComparison.Ordinal)
                && fov.Contains("9.75", StringComparison.Ordinal) && !fov.Contains("70,25", StringComparison.Ordinal)
                && CultureInfo.CurrentCulture.Name == "fr-FR",
                "selected language formats diagnostic numbers consistently without changing the caller's numeric culture");
            check(L10n.T("Runtime.Session.FovEffective", 60.5, 70.25, "unreadable", 9.75, 100)
                .Contains("unreadable", StringComparison.Ordinal),
                "a missing stored FOV keeps its translated text instead of requiring a numeric value");
            CultureInfo.CurrentCulture = originalNumberCulture;

            var config = JsonSerializer.Deserialize<UnlockConfig>("{}");
            config.Normalize();
            check(config.Language == "auto", "existing settings without a language continue following the system");
            config.Language = "ja";
            config.Normalize();
            var clone = config.Clone();
            var restored = JsonSerializer.Deserialize<UnlockConfig>(JsonSerializer.Serialize(clone));
            check(clone.Language == "ja-JP" && restored.Language == "ja-JP"
                && clone.ProcessPath == config.ProcessPath
                && clone.TargetFramerate == config.TargetFramerate,
                "language choice survives session snapshots and JSON persistence without changing game settings");
            config.Language = "unsupported";
            config.Normalize();
            check(config.Language == "auto", "unsupported saved languages fall back to automatic selection");

            var assembly = typeof(L10n).Assembly;
            string[] resources = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("HSRUnlocker.Localization.", StringComparison.Ordinal)
                    && name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
            check(resources.Length >= 3, "all UI and runtime translation catalogs are embedded in the shipped assembly");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int entries = 0;
            foreach (string resource in resources)
            {
                int catalogEntries = 0;
                using var stream = assembly.GetManifestResourceStream(resource);
                using var catalog = JsonDocument.Parse(stream);
                foreach (var entry in catalog.RootElement.EnumerateObject())
                {
                    Require(keys.Add(entry.Name), $"translation key {entry.Name} has one unambiguous owner");
                    string[] expectedSlots = null;
                    foreach (string language in languages)
                    {
                        Require(entry.Value.TryGetProperty(language, out var translation)
                            && translation.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(translation.GetString()),
                            $"{entry.Name} has a usable {language} translation");
                        string text = translation.GetString();
                        var slots = Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
                            .Select(match => match.Groups[1].Value).Distinct().OrderBy(value => value).ToArray();
                        if (expectedSlots == null) expectedSlots = slots;
                        Require(slots.SequenceEqual(expectedSlots),
                            $"{entry.Name} preserves every format value in {language}");
                        object[] values = Enumerable.Repeat<object>(7, 16).ToArray();
                        string expected = string.Format(CultureInfo.GetCultureInfo(language), text, values);
                        L10n.SetLanguage(language);
                        Require(L10n.T(entry.Name, values) == expected,
                            $"{entry.Name} resolves and formats its embedded {language} catalog entry");
                    }
                    entries++;
                    catalogEntries++;
                }
                check(catalogEntries > 0,
                    $"{resource} has unique keys, complete four-language translations and consistent format values");
            }
            check(entries > 0, "translated user messages are available at runtime");

            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            Parallel.For(0, 200, i =>
            {
                try
                {
                    L10n.SetLanguage(languages[i % languages.Length]);
                    string value = L10n.T("Ui.StatusActive", 144, 1234);
                    if (!value.Contains("144") || !value.Contains("1234")) throw new Exception("Lost format values.");
                }
                catch (Exception error) { errors.Enqueue(error); }
            });
            check(errors.IsEmpty, "background diagnostics remain usable while the UI changes language");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            CultureInfo.CurrentCulture = originalNumberCulture;
            L10n.SetLanguage(originalPreference);
        }
    }

    private static void Require(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + description);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace StarRailUnlocker.Core
{
    public static class L10n
    {
        private sealed record LanguageState(string Preference, string Language, CultureInfo Culture);

        private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Catalog =
            new(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);
        private static LanguageState _state = CreateState("auto");

        public static string Preference => Volatile.Read(ref _state).Preference;
        public static string Language => Volatile.Read(ref _state).Language;

        public static string NormalizePreference(string value)
        {
            value = value?.Trim().Replace('_', '-');
            if (string.IsNullOrEmpty(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return "auto";
            if (value.Split('-').Any(part => part.Length == 0 || part.Length > 8
                || part.Any(character => !char.IsAsciiLetterOrDigit(character))))
                return "auto";
            try
            {
                return SupportedLanguage(CultureInfo.GetCultureInfo(value).TwoLetterISOLanguageName) ?? "auto";
            }
            catch (CultureNotFoundException)
            {
                return "auto";
            }
        }

        public static void SetLanguage(string preference)
        {
            Interlocked.Exchange(ref _state, CreateState(NormalizePreference(preference)));
        }

        public static string T(string key, params object[] args)
        {
            ArgumentNullException.ThrowIfNull(key);
            LanguageState state = Volatile.Read(ref _state);
            if (!Catalog.Value.TryGetValue(key, out var translations)) return key;
            if (!translations.TryGetValue(state.Language, out string text))
                text = translations["en-US"];
            return args == null || args.Length == 0 ? text : string.Format(state.Culture, text, args);
        }

        private static LanguageState CreateState(string preference)
        {
            string language = preference == "auto"
                ? SupportedLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? "en-US"
                : preference;
            return new LanguageState(preference, language, CultureInfo.GetCultureInfo(language));
        }

        private static string SupportedLanguage(string language) => language.ToLowerInvariant() switch
        {
            "zh" => "zh-CN",
            "en" => "en-US",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            _ => null
        };

        private static Dictionary<string, Dictionary<string, string>> LoadCatalog()
        {
            var catalog = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var assembly = typeof(L10n).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("HSRUnlocker.Localization.", StringComparison.Ordinal)
                    && name.EndsWith(".json", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                using Stream stream = assembly.GetManifestResourceStream(resource);
                using JsonDocument document = JsonDocument.Parse(stream);
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    var translations = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var translation in entry.Value.EnumerateObject())
                    {
                        string text = translation.Value.GetString();
                        if (string.IsNullOrWhiteSpace(text))
                            throw new InvalidDataException($"Empty translation: {entry.Name}/{translation.Name}");
                        CompositeFormat.Parse(text);
                        if (!translations.TryAdd(translation.Name, text))
                            throw new InvalidDataException($"Duplicate translation: {entry.Name}/{translation.Name}");
                    }
                    if (!translations.ContainsKey("en-US"))
                        throw new InvalidDataException($"Missing English fallback: {entry.Name}");
                    if (!catalog.TryAdd(entry.Name, translations))
                        throw new InvalidDataException($"Duplicate translation key: {entry.Name}");
                }
            }
            return catalog;
        }
    }
}

using System;
using System.Collections.Generic;

namespace StarRailUnlocker.Core
{
    // UI-thread state for the dismissible startup notice. Manual checks stay inline.
    internal sealed class UpdateNotice
    {
        private readonly HashSet<string> dismissed = new(StringComparer.OrdinalIgnoreCase);
        private UpdateCheckResult result;

        internal void Offer(UpdateCheckResult value, bool background) =>
            result = background && value?.IsNewer == true && !dismissed.Contains(Normalize(value.Version)) ? value : null;

        internal UpdateCheckResult Visible(bool windowReady) => windowReady ? result : null;

        internal void Dismiss()
        {
            if (result != null) dismissed.Add(Normalize(result.Version));
            result = null;
        }

        private static string Normalize(string version)
        {
            string text = (version ?? string.Empty).Trim().TrimStart('v', 'V');
            return Version.TryParse(text, out Version parsed)
                ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0)).ToString()
                : text;
        }
    }
}

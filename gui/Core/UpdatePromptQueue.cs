using System;
using System.Collections.Generic;

namespace StarRailUnlocker.Core
{
    // Owned by the main window for the process lifetime; all access is on its UI thread.
    internal sealed class UpdatePromptQueue
    {
        private readonly HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
        private UpdateCheckResult pending;

        public void Offer(UpdateCheckResult result) =>
            pending = result?.IsNewer == true && !shown.Contains(Normalize(result.Version)) ? result : null;

        public UpdateCheckResult Take(bool canShow)
        {
            if (!canShow || pending == null) return null;
            UpdateCheckResult result = pending;
            pending = null;
            shown.Add(Normalize(result.Version));
            return result;
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

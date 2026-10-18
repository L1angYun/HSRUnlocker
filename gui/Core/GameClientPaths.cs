using System;
using System.Collections.Generic;
using System.IO;

namespace StarRailUnlocker.Core
{
    /// <summary>Bounded, ordered client paths; existence checks are supplied by the caller.</summary>
    public static class GameClientPaths
    {
        private const int MaxPaths = 64;
        private const int MaxExaminedPaths = 1024;
        private const int MaxPathLength = 32767;

        public static string[] Normalize(IEnumerable<string> paths)
        {
            var normalized = new List<string>();
            Append(paths, normalized, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
            return normalized.ToArray();
        }

        public static string[] Available(string current, IEnumerable<string> remembered,
            IEnumerable<string> discovered, Func<string, bool> fileExists)
        {
            if (fileExists == null) return Array.Empty<string>();
            var available = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Append(new[] { current }, available, seen, fileExists);
            Append(remembered, available, seen, fileExists);
            Append(discovered, available, seen, fileExists);
            return available.ToArray();
        }

        private static void Append(IEnumerable<string> candidates, List<string> result,
            HashSet<string> seen, Func<string, bool> fileExists)
        {
            if (candidates == null || result.Count >= MaxPaths) return;
            try
            {
                using var enumerator = candidates.GetEnumerator();
                int examined = 0;
                while (result.Count < MaxPaths && examined < MaxExaminedPaths && enumerator.MoveNext())
                {
                    examined++;
                    string path = LocalExecutable(enumerator.Current);
                    if (path == null || !seen.Add(path)) continue;
                    if (fileExists != null)
                    {
                        try { if (!fileExists(path)) continue; }
                        catch (Exception) { continue; }
                    }
                    result.Add(path);
                }
            }
            catch (Exception)
            {
                // A malformed source must not discard clients already collected.
            }
        }

        private static string LocalExecutable(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length >= MaxPathLength) return null;
            string path = value.Trim();
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path[2..].Contains(':'))
                return null;
            foreach (char character in path)
                if (char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*')
                    return null;
            try
            {
                string full = Path.GetFullPath(path);
                return full.Length < MaxPathLength &&
                    Path.GetFileName(full).Equals("StarRail.exe", StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception) { return null; }
        }
    }
}

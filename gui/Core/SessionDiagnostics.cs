using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StarRailUnlocker.Core
{
    internal enum DiagnosticStep
    {
        Session, WaitForGame, OpenGame, WaitForModules, ResolveFrameCap,
        InitialWrite, InitialReadback, MonitorFrameCap, UpdateFrameCap,
        RestoreFrameCap, RestoreRead, RestoreWrite, RestoreReadback, RestoreSession
    }

    internal enum DiagnosticResult
    {
        Started, Ready, Applied, Reapplied, Recovered, TransferFailed, Retry,
        InvalidValue, Mismatch, Confirmed, Failed, Pending, Stopped, GameExited
    }

    /// <summary>Exports only structured values; never copies the window or disk log.</summary>
    internal sealed class SessionDiagnostics
    {
        private const int RecentLimit = 120;
        private readonly object _gate = new();
        private readonly Queue<Entry> _recent = new();
        private Entry? _first;
        private Entry? _firstFailure;
        private long _sequence, _omitted;

        private readonly record struct Entry(long Sequence, DateTime Time, DiagnosticStep Step, DiagnosticResult Result,
            Int32Transfer? Transfer, int? Expected, int? Observed, (int Previous, int Requested)? Transition);

        internal bool HasEntries { get { lock (_gate) return _first.HasValue; } }

        internal void Reset()
        {
            lock (_gate)
            {
                _first = _firstFailure = null;
                _recent.Clear();
                _sequence = _omitted = 0;
            }
        }

        internal void Add(DiagnosticStep step, DiagnosticResult result) => Record(step, result);
        internal void AddTransfer(DiagnosticStep step, Int32Transfer transfer, bool retry = false) =>
            Record(step, retry ? DiagnosticResult.Retry : DiagnosticResult.TransferFailed, transfer);
        internal void AddValues(DiagnosticStep step, DiagnosticResult result, int? expected, int observed) =>
            Record(step, result, expected: expected, observed: observed);
        internal void AddTransition(DiagnosticStep step, DiagnosticResult result, int previous, int requested) =>
            Record(step, result, transition: (previous, requested));

        private void Record(DiagnosticStep step, DiagnosticResult result,
            Int32Transfer? transfer = null, int? expected = null, int? observed = null, (int, int)? transition = null)
        {
            lock (_gate)
            {
                var entry = new Entry(++_sequence, DateTime.UtcNow, step, result, transfer, expected, observed, transition);
                if (!_first.HasValue) { _first = entry; return; }
                if (!_firstFailure.HasValue && result is (DiagnosticResult.Failed
                    or DiagnosticResult.TransferFailed or DiagnosticResult.Mismatch or DiagnosticResult.InvalidValue))
                {
                    _firstFailure = entry;
                    return;
                }
                if (_recent.Count == RecentLimit) { _recent.Dequeue(); _omitted++; }
                _recent.Enqueue(entry);
            }
        }

        internal string CreateReport(string appVersion)
        {
            Entry first;
            Entry? firstFailure;
            Entry[] recent;
            long omitted;
            lock (_gate)
            {
                if (!_first.HasValue) return "";
                first = _first.Value;
                firstFailure = _firstFailure;
                recent = _recent.ToArray();
                omitted = _omitted;
            }

            var entries = new List<Entry>(recent.Length + 2) { first };
            if (firstFailure.HasValue) entries.Add(firstFailure.Value);
            entries.AddRange(recent);
            entries.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            var report = new StringBuilder()
                .AppendLine("HSRUnlocker diagnostics v1")
                .AppendLine($"HSRUnlocker: {(Version.TryParse(appVersion, out var version) ? version.ToString() : "unknown")}")
                .AppendLine($"Windows: {Environment.OSVersion.Version}");
            if (omitted != 0)
                report.AppendLine(FormattableString.Invariant($"Earlier events omitted: {omitted}"));
            report.AppendLine();
            foreach (var entry in entries)
            {
                report.Append(entry.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                    .Append(' ').Append(entry.Step).Append(' ').Append(entry.Result);
                if (entry.Transfer is { } transfer)
                {
                    if (transfer.Win32Error != 0)
                        report.Append(FormattableString.Invariant($" win32={transfer.Win32Error}"));
                    report.Append(FormattableString.Invariant($" bytes={transfer.BytesTransferred}/{sizeof(int)}"));
                }
                if (entry.Expected is int expected)
                    report.Append(FormattableString.Invariant($" expected={expected}"));
                if (entry.Observed is int observed)
                    report.Append(FormattableString.Invariant($" observed={observed}"));
                if (entry.Transition is { } transition)
                    report.Append(FormattableString.Invariant($" previous={transition.Previous} requested={transition.Requested}"));
                report.AppendLine();
            }
            return report.ToString();
        }
    }
}

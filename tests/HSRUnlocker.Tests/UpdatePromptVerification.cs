using StarRailUnlocker.Core;

internal static class UpdatePromptVerification
{
    public static void Run(Action<bool, string> check)
    {
        var queue = new UpdatePromptQueue();
        var update = Result("1.2.0");
        queue.Offer(update);
        check(queue.Take(false) == null && queue.Take(false) == null,
            "update prompt stays deferred while the window cannot show it");
        check(ReferenceEquals(queue.Take(true), update),
            "a deferred update becomes available when the window can show it");
        check(queue.Take(true) == null, "taking an update prompt consumes its pending result");
        queue.Offer(Result("1.2.0"));
        check(queue.Take(true) == null, "the same shown version does not prompt again");
        queue.Offer(Result("v1.2.0.0"));
        check(queue.Take(true) == null, "equivalent normalized version spellings share prompt history");
        var next = Result("1.2.1");
        queue.Offer(next);
        check(ReferenceEquals(queue.Take(true), next), "a newly offered version can prompt after an earlier version");
        queue.Offer(update);
        check(queue.Take(true) == null, "showing a later version retains history for earlier shown versions");

        var clearing = new UpdatePromptQueue();
        clearing.Offer(update);
        clearing.Offer(null);
        check(clearing.Take(true) == null, "a null check result clears a pending update prompt");
        clearing.Offer(update);
        clearing.Offer(Result("1.1.0", false));
        check(clearing.Take(true) == null, "a current-version check clears a pending update prompt");
        clearing.Offer(update);
        check(ReferenceEquals(clearing.Take(true), update),
            "cleared and deferred results are not recorded as already shown");

        var replacing = new UpdatePromptQueue();
        replacing.Offer(update);
        replacing.Offer(next);
        check(ReferenceEquals(replacing.Take(true), next), "a fresh update check replaces the one pending result");
        replacing.Offer(update);
        check(ReferenceEquals(replacing.Take(true), update), "a replaced result was never marked shown");
    }

    private static UpdateCheckResult Result(string version, bool newer = true) =>
        new(version, newer, new Uri("https://example.invalid/releases/" + version));
}

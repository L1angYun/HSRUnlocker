using StarRailUnlocker.Core;

internal static class UpdateNoticeVerification
{
    public static void Run(Action<bool, string> check)
    {
        var notice = new UpdateNotice();
        var first = Result("1.3.8");
        notice.Offer(first, background: false);
        check(notice.Visible(true) == null, "a manual update check never opens the startup notice");
        notice.Offer(first, background: true);
        check(notice.Visible(false) == null && ReferenceEquals(notice.Visible(true), first),
            "an automatic update notice waits while the window is not ready and then appears without consuming the result");
        check(notice.Visible(false) == null && ReferenceEquals(notice.Visible(true), first),
            "using the app temporarily hides the automatic notice without dismissing it");
        notice.Dismiss();
        notice.Offer(Result("v1.3.8.0"), background: true);
        check(notice.Visible(true) == null, "dismissal survives equivalent spellings of the same version");
        var next = Result("1.3.9");
        notice.Offer(next, background: true);
        check(ReferenceEquals(notice.Visible(true), next), "dismissing one version leaves the next version eligible");
        notice.Offer(null, background: true);
        check(notice.Visible(true) == null, "resetting a check clears displayed metadata");
        notice.Offer(Result("1.3.7", newer: false), background: true);
        check(notice.Visible(true) == null, "an up-to-date background check does not show a notice");
    }

    private static UpdateCheckResult Result(string version, bool newer = true) =>
        new(version, newer, new Uri("https://example.invalid/releases/" + version));
}

using StarRailUnlocker.Core;

internal static class MobileSafeAreaOptionsVerification
{
    public static void Run(Action<bool, string> check)
    {
        object Resolve(MobileSafeAreaMode mode, double percent) =>
            MobileSafeArea.ResolveRegistration(null, mode, percent);
        check(Resolve(MobileSafeAreaMode.Original, 5) is null
            && Resolve(MobileSafeAreaMode.Original, double.NaN) is null
            && Resolve((MobileSafeAreaMode)99, 5) is null,
            "original and unknown safe-area modes do not resolve an optional getter or validate inactive percentages");
        check(Resolve(MobileSafeAreaMode.Custom, 0) is null,
            "zero-percent custom safe area does not resolve or install any override");
        foreach (double percent in new[] { -1d, 15.01, double.NaN, double.PositiveInfinity })
        {
            bool refused = false;
            try { Resolve(MobileSafeAreaMode.Custom, percent); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            check(refused, $"active safe-area percentage {percent} is rejected before attempting name resolution");
        }
        bool imageRequired = false;
        try { Resolve(MobileSafeAreaMode.Custom, 5); }
        catch (ArgumentNullException) { imageRequired = true; }
        check(imageRequired, "positive custom safe area requires a verified engine image rather than a fixed address");
    }
}

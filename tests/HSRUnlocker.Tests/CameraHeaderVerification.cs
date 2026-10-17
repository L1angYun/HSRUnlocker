using StarRailUnlocker.Core;

internal static class CameraHeaderVerification
{
    public static void Run(Action<bool, string> check)
    {
        var disk = new byte[264];
        disk[0] = 0x50; disk[1] = 0x45;
        disk[4] = 0x64; disk[5] = 0x86;
        disk[24] = 0x0B; disk[25] = 0x02;
        BitConverter.GetBytes(0x180000000UL).CopyTo(disk, 48);
        const ulong loadedBase = 0x7FF908D30000UL;
        var mapped = (byte[])disk.Clone();
        BitConverter.GetBytes(loadedBase).CopyTo(mapped, 48);
        check(CameraFadeResolver.MatchesMappedHeaders(disk, mapped, loadedBase),
            "camera identity accepts a loader-rebased PE32+ ImageBase matching the actual mapped module");
        check(CameraFadeResolver.MatchesMappedHeaders(disk, disk, loadedBase),
            "camera identity also accepts an unchanged preferred ImageBase header");
        check(!CameraFadeResolver.MatchesMappedHeaders(disk, mapped, loadedBase + 0x10000),
            "camera identity refuses a rebased header that does not match the actual module base");
        foreach (int offset in new[] { 0, 8, 24, 47, 56, 80, 263 })
        {
            var changed = (byte[])mapped.Clone();
            changed[offset] ^= 1;
            check(!CameraFadeResolver.MatchesMappedHeaders(disk, changed, loadedBase),
                $"camera identity still refuses a changed header byte outside ImageBase at offset {offset}");
        }
        check(!CameraFadeResolver.MatchesMappedHeaders(disk, null, loadedBase)
            && !CameraFadeResolver.MatchesMappedHeaders(disk, mapped.AsSpan(0, 55).ToArray(), loadedBase),
            "camera identity refuses unreadable or truncated mapped headers");
    }
}

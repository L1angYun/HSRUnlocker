using System.Security.Cryptography;
using StarRailUnlocker.Core;

internal static class CameraBodyVerification
{
    private const string GoldenDigest = "42C3E787245C7B16435BDA9509968BEC004B6B3A53C4E24780D8C70DE72A634A";
    private const string SingleByteDigest = "087D80F7F182DD44F184AA86CA34488853EBCC04F0C60D5294919A466B463831";

    public static void Run(Action<bool, string> check)
    {
        byte[] input = { 0xA5, 0x11, 0x22, 0x33, 0x5A, 0xFF, 0x7E, 0x81 };
        byte[] original = (byte[])input.Clone();
        var masks = new[] { (Offset: 1, Length: 3), (Offset: 5, Length: 2) };
        var shape = new CameraFadeResolver.BodyShape(input.Length, GoldenDigest, masks);
        check(shape.Matches(input), "camera body fingerprint accepts an independently normalized authored eight-byte fixture");
        check(input.AsSpan().SequenceEqual(original), "camera body matching does not normalize or otherwise modify its caller's input");

        for (int at = 0; at < input.Length; at++)
        {
            bool wildcard = at is 1 or 2 or 3 or 5 or 6;
            bool correct = true, unchanged = true;
            for (int value = 0; value <= byte.MaxValue; value++)
            {
                var changed = (byte[])original.Clone();
                changed[at] = (byte)value;
                var before = (byte[])changed.Clone();
                correct &= shape.Matches(changed) == (wildcard || value == original[at]);
                unchanged &= changed.AsSpan().SequenceEqual(before);
            }
            check(correct, $"camera body fingerprint {(wildcard ? "accepts every wildcard value" : "refuses every changed fixed value")} at authored offset {at}");
            check(unchanged, $"camera body matching leaves all 256 authored inputs unchanged at offset {at}");
        }

        check(!shape.Matches(null) && !shape.Matches(Array.Empty<byte>())
            && !shape.Matches(original.AsSpan(0, original.Length - 1).ToArray())
            && !shape.Matches(original.Concat(new byte[] { 0 }).ToArray()),
            "camera body fingerprint refuses null, empty, truncated and extended input rather than accepting a matching prefix");
        check(new CameraFadeResolver.BodyShape(input.Length, GoldenDigest.ToLowerInvariant(), masks).Matches(input),
            "camera body fingerprint accepts a valid lowercase SHA-256 descriptor");
        check(!new CameraFadeResolver.BodyShape(input.Length, new string('0', 64), masks).Matches(input),
            "camera body fingerprint refuses a full-length fixture whose normalized digest differs");
        check(new CameraFadeResolver.BodyShape(input.Length, GoldenDigest, (1, 1), (2, 2), (5, 2)).Matches(input),
            "camera body fingerprint preserves normalization across adjacent nonoverlapping wildcard ranges");

        byte[] normalizedEdges = { 0, 0x11, 0x22, 0x33, 0x5A, 0xFF, 0x7E, 0 };
        var edges = new CameraFadeResolver.BodyShape(input.Length,
            Convert.ToHexString(SHA256.HashData(normalizedEdges)), (0, 1), (7, 1));
        var edgeVariant = (byte[])input.Clone();
        edgeVariant[0] ^= 0xFF; edgeVariant[7] ^= 0xFF;
        check(edges.Matches(edgeVariant), "camera body fingerprint clears valid wildcard ranges at both input boundaries");

        masks[0] = (0, 1);
        var changedFixed = (byte[])input.Clone(); changedFixed[0] ^= 1;
        check(shape.Matches(input) && !shape.Matches(changedFixed),
            "camera body fingerprint retains its validated ranges when the caller later changes the supplied range array");

        var unmasked = new CameraFadeResolver.BodyShape(1, SingleByteDigest);
        check(unmasked.Matches(new byte[] { 0xAB }) && !unmasked.Matches(new byte[] { 0xAA }),
            "camera body fingerprint supports a one-byte, fully fixed body without accepting a changed byte");
        var large = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var largest = new CameraFadeResolver.BodyShape(large.Length, Convert.ToHexString(SHA256.HashData(large)));
        var changedLarge = (byte[])large.Clone(); changedLarge[^1] ^= 1;
        check(largest.Matches(large) && !largest.Matches(changedLarge),
            "camera body fingerprint bounds the largest supported authored body and verifies its final byte");

        void Reject(string description, Action construct)
        {
            bool rejected = false;
            try { construct(); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, description);
        }
        foreach (int length in new[] { -1, 0, 4097, int.MaxValue })
            Reject($"camera body descriptor refuses unsupported length {length}",
                () => _ = new CameraFadeResolver.BodyShape(length, GoldenDigest));
        foreach (string digest in new[] { null, "", new string('0', 62), new string('0', 66), new string('G', 64) })
            Reject("camera body descriptor refuses a missing, incorrectly sized or nonhexadecimal SHA-256",
                () => _ = new CameraFadeResolver.BodyShape(8, digest));
        Reject("camera body descriptor refuses a null wildcard range array",
            () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, null));
        foreach (var invalid in new[] { (-1, 1), (1, 0), (1, -1), (8, 1), (7, 2), (int.MaxValue, 1), (1, int.MaxValue) })
            Reject($"camera body descriptor refuses invalid wildcard range ({invalid.Item1}, {invalid.Item2})",
                () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, invalid));
        Reject("camera body descriptor refuses overlapping wildcard ranges",
            () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, (1, 3), (3, 2)));
        Reject("camera body descriptor refuses wildcard ranges supplied out of order",
            () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, (5, 2), (1, 3)));
        Reject("camera body descriptor refuses one wildcard range covering every byte",
            () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, (0, 8)));
        Reject("camera body descriptor refuses adjacent wildcard ranges covering every byte",
            () => _ = new CameraFadeResolver.BodyShape(8, GoldenDigest, (0, 4), (4, 4)));
    }
}

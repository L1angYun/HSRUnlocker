using StarRailUnlocker.Core;
using Region = StarRailUnlocker.Core.MappedImageResolver.Region;

internal static class MappedImageVerification
{
    private const uint Commit = 0x1000;
    private const uint Free = 0x10000;
    private const uint Image = 0x1000000;
    private const uint Private = 0x20000;
    private const uint Mapped = 0x40000;
    private const string Expected = @"\Device\HarddiskVolume9\Synthetic\GameAssembly.dll";

    public static void Run(Action<bool, string> check)
    {
        check(RemoteFunctionTable.ImageSizeMatches(0x26C000, 0x26C000, true),
            "mapped image extent accepts an exact logical PE image size");
        check(RemoteFunctionTable.ImageSizeMatches(0x271000, 0x26C000, true),
            "mapped image extent accepts native allocation tail pages beyond logical PE size");
        check(!RemoteFunctionTable.ImageSizeMatches(0x26B000, 0x26C000, true),
            "mapped image extent cannot be smaller than the logical PE image size");
        check(RemoteFunctionTable.ImageSizeMatches(0x26C000, 0x26C000, false),
            "Toolhelp module size accepts exact logical PE size");
        check(!RemoteFunctionTable.ImageSizeMatches(0x271000, 0x26C000, false) &&
            !RemoteFunctionTable.ImageSizeMatches(0x26B000, 0x26C000, false),
            "Toolhelp module size rejects both larger and smaller logical PE size mismatches");
        check(!RemoteFunctionTable.ImageSizeMatches(0, 0, true) &&
            !RemoteFunctionTable.ImageSizeMatches(0x271000, 0, true) &&
            !RemoteFunctionTable.ImageSizeMatches(0, 0, false),
            "zero logical PE image size is never accepted for either size source");

        Region[] multi =
        [
            new(0x1000, 0, 0x1000, Free, 0, 0),
            new(0x2000, 0x2000, 0x1000, Commit, 0x02, Image),
            new(0x3000, 0x2000, 0x1000, Commit, 0x20, Image),
            new(0x4000, 0, 0x1000, Free, 0, 0)
        ];
        int nameCalls = 0;
        ulong lastQuery = 0;
        var found = MappedImageResolver.Find(Expected, 0x1000, 0x4FFF,
            address => { lastQuery = address; return Query(multi, address); },
            _ => { nameCalls++; return Expected.ToUpperInvariant(); });
        check(found == (0x2000UL, 0x2000U),
            "mapped image resolves exact case-insensitive NT path across protection regions");
        check(nameCalls == 1,
            "same-allocation protection regions are one image rather than duplicate matches");
        check(lastQuery == 0x4000,
            "mapped image lookup finishes the entire inclusive bounded range before accepting uniqueness");
        check(MappedImageResolver.Find(Expected, 0x1000, 0x4FFF, address => Query(multi, address),
            _ => Expected + ".old") == (0UL, 0U),
            "mapped image name matching rejects a prefix-similar different file");
        check(MappedImageResolver.Find(Expected, 0x1000, 0x1FFF,
            address => Query(multi, address), _ => throw new InvalidOperationException("No image name expected")) == (0UL, 0U),
            "an entirely unmapped range returns no image without requesting a filename");

        foreach ((uint type, string label) in new[] { (Private, "MEM_PRIVATE"), (Mapped, "MEM_MAPPED") })
        {
            Region[] other = [new(0x1000, 0x1000, 0x1000, Commit, 0x20, type)];
            check(MappedImageResolver.Find(Expected, 0x1000, 0x1FFF,
                address => Query(other, address), _ => throw new InvalidOperationException("Wrong type queried")) == (0UL, 0U),
                "mapped image lookup refuses " + label + " even when executable");
        }
        Region[] uncommitted = [new(0x1000, 0x1000, 0x1000, 0x2000, 0x20, Image)];
        check(MappedImageResolver.Find(Expected, 0x1000, 0x1FFF,
            address => Query(uncommitted, address), _ => throw new InvalidOperationException("Uncommitted image queried")) == (0UL, 0U),
            "uncommitted image allocation is not a filename candidate");
        Region[] continuation = [new(0x1000, 0x0800, 0x1000, Commit, 0x20, Image)];
        check(MappedImageResolver.Find(Expected, 0x1000, 0x1FFF,
            address => Query(continuation, address), _ => throw new InvalidOperationException("Continuation queried")) == (0UL, 0U),
            "an image continuation cannot impersonate its absent allocation base");

        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x4FFF,
            address => Query(multi, address), _ => null)),
            "missing mapped filename fails closed rather than reporting an absent image");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x4FFF,
            address => Query(multi, address), _ => "")),
            "empty mapped filename fails closed");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x4FFF,
            address => Query(multi, address), _ => new string('X', 32768))),
            "potentially truncated mapped filename at the native bound fails closed");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x4FFF,
            address => Query(multi, address), _ => new string('X', 32769))),
            "overlong mapped filename fails closed");

        Region[] duplicate =
        [
            new(0x1000, 0x1000, 0x1000, Commit, 0x20, Image),
            new(0x2000, 0, 0x1000, Free, 0, 0),
            new(0x3000, 0x3000, 0x1000, Commit, 0x20, Image)
        ];
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x3FFF,
            address => Query(duplicate, address), _ => Expected)),
            "two separate matching image allocations are ambiguous and refused");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x2000, 0x1000,
            address => Query(multi, address), _ => Expected)),
            "reversed address bounds are rejected");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x1FFF,
            _ => new Region(0x1000, 0, 0, Free, 0, 0), _ => Expected)),
            "zero-sized query region cannot stall the address walk");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x2000, 0x2FFF,
            _ => new Region(0x1000, 0, 0x1000, Free, 0, 0), _ => Expected)),
            "query region ending at the cursor cannot stall the address walk");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x1000, 0x2FFF,
            _ => new Region(0x2000, 0, 0x1000, Free, 0, 0), _ => Expected)),
            "query region skipping the requested address cannot silently omit a gap");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, ulong.MaxValue - 15, ulong.MaxValue,
            _ => new Region(ulong.MaxValue - 15, 0, 32, Free, 0, 0), _ => Expected)),
            "overflowing region extent fails closed");
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 0x2000, 0x4FFF,
            address => address < 0x4000 ? Query(multi, address) :
                throw new InvalidOperationException("Synthetic incomplete address walk"), _ => Expected)),
            "query failure after a matching allocation cannot return a partial-scan success");
        int boundedQueries = 0;
        check(InvalidFind(() => MappedImageResolver.Find(Expected, 1, 65537,
            address => { boundedQueries++; return new Region(address, 0, 1, Free, 0, 0); }, _ => Expected))
            && boundedQueries <= 65537,
            "excessively fragmented address space is refused within the bounded query budget");

        foreach (uint protection in new uint[] { 0x10, 0x20, 0x40, 0x80 })
        {
            Region[] executable = [new(0x2000, 0x2000, 0x1000, Commit, protection, Image)];
            check(MappedImageResolver.IsExecutableRange(0x2000, 0x2010, 16,
                address => Query(executable, address)),
                "entry range accepts committed unguarded executable image protection " + protection);
        }
        Region[] splitExecutable =
        [
            new(0x2000, 0x2000, 0x1000, Commit, 0x20, Image),
            new(0x3000, 0x2000, 0x1000, Commit, 0x40, Image)
        ];
        check(MappedImageResolver.IsExecutableRange(0x2000, 0x2FF8, 16,
            address => Query(splitExecutable, address)),
            "entry validation spans contiguous executable regions of the same image");
        foreach ((uint state, uint protection, uint type, ulong allocation, string label) in new[]
        {
            (Commit, 0x04U, Image, 0x2000UL, "non-executable page"),
            (Commit, 0x120U, Image, 0x2000UL, "guard page"),
            (0x2000U, 0x20U, Image, 0x2000UL, "uncommitted page"),
            (Commit, 0x20U, Private, 0x2000UL, "private page"),
            (Commit, 0x20U, Mapped, 0x2000UL, "mapped data page"),
            (Commit, 0x20U, Image, 0x1000UL, "different image allocation")
        })
        {
            Region[] refused = [new(0x2000, allocation, 0x1000, state, protection, type)];
            check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2010, 16,
                address => Query(refused, address))), "entry range refuses " + label);
        }
        Region[] splitWrong =
        [
            new(0x2000, 0x2000, 0x1000, Commit, 0x20, Image),
            new(0x3000, 0x3000, 0x1000, Commit, 0x20, Image)
        ];
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2FF8, 16,
            address => Query(splitWrong, address))),
            "cross-region entry cannot extend into a different image allocation");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2FF8, 16,
            address => address < 0x3000 ? splitExecutable[0] :
                new Region(0x3001, 0x2000, 0x1000, Commit, 0x20, Image))),
            "cross-region query gap is refused");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2000, 0,
            address => Query(splitExecutable, address))),
            "zero-length executable range is refused");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2000, 4097,
            address => Query(splitExecutable, address))),
            "entry validation refuses lengths above the 4096-byte bound");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, ulong.MaxValue - 7, 16,
            _ => new Region(ulong.MaxValue - 7, 0x2000, 8, Commit, 0x20, Image))),
            "overflowing executable range is refused");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2000, 16,
            _ => new Region(0x2000, 0x2000, 0, Commit, 0x20, Image))),
            "zero-sized executable query region is refused");
        check(RefusesRange(() => MappedImageResolver.IsExecutableRange(0x2000, 0x2000, 16,
            _ => throw new InvalidOperationException("Synthetic entry query failure"))),
            "entry query failures never authorize executable memory");
    }

    private static Region Query(Region[] regions, ulong address)
    {
        foreach (Region region in regions)
            if (address >= region.Base && address - region.Base < region.Size) return region;
        throw new InvalidOperationException("Synthetic query omitted an address.");
    }

    private static bool InvalidFind(Func<(ulong Base, uint Size)> action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool RefusesRange(Func<bool> action)
    {
        try { return !action(); }
        catch (Exception) { return true; }
    }
}

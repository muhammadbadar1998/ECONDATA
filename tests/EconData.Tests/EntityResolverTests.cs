using EconData.Core.Entities;

namespace EconData.Tests;

public class EntityResolverTests
{
    private static EntityResolver CreateResolver()
        => new(EntitySeed.Load(TestPaths.ConfigFile("entities.json")));

    [Fact]
    public void ResolvesKnownCuratedAndIso3Codes()
    {
        var resolver = CreateResolver();

        Assert.Equal("WLD", resolver.ResolveCode("WB", "WLD"));
        Assert.Equal("USA", resolver.ResolveCode("OECD", "USA"));
        Assert.Equal("XYZ", resolver.ResolveCode("WB", "XYZ"));
        Assert.Null(resolver.ResolveCode("WB", "ANNUAL"));
    }

    [Fact]
    public void RegistersUnknownCodesDeterministically()
    {
        var resolver = CreateResolver();

        var code = resolver.RegisterUnknown("WB", "ZZZ", "Newland");
        Assert.Equal("ZZZ", code);
        Assert.Equal("Newland", resolver.EntityName("ZZZ"));
    }

    [Fact]
    public void RefusesNonEntityCodes()
    {
        var resolver = CreateResolver();

        Assert.Equal("ANNUAL", resolver.RegisterUnknown("WB", "ANNUAL", "Annual"));
        Assert.False(resolver.IsKnownEntity("ANNUAL"));
    }
}

using EconData.Core.Entities;

namespace EconData.Tests;

public class EntitySeedTests
{
    private static EntitySeed Load() => EntitySeed.Load(TestPaths.ConfigFile("entities.json"));

    [Fact]
    public void LoadsExpectedCounts()
    {
        var seed = Load();

        Assert.Equal(80, seed.Entities.Count);
        Assert.Equal(97, seed.Aliases.Count);
        Assert.Equal(77, seed.MembershipCount);
    }

    [Fact]
    public void ContainsWorldAggregateAndItsAlias()
    {
        var seed = Load();

        Assert.Contains(seed.Entities, entity => entity.EntityCode == "WLD" && entity.IsAggregate);
        Assert.Contains(seed.Aliases, alias => alias.Provider == "WB" && alias.ProviderCode == "WLD" && alias.EntityCode == "WLD");
        Assert.True(seed.Memberships.ContainsKey("OECD"));
    }
}

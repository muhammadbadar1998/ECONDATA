using EconData.Core.Configuration;
using Xunit;

namespace EconData.Tests;

public class ServingStoreTests
{
    [Theory]
    [InlineData(null, ServingStore.Auto)]
    [InlineData("", ServingStore.Auto)]
    [InlineData("   ", ServingStore.Auto)]
    [InlineData("auto", ServingStore.Auto)]
    [InlineData("AUTO", ServingStore.Auto)]
    [InlineData("Auto", ServingStore.Auto)]
    [InlineData("gold", ServingStore.Gold)]
    [InlineData("GOLD", ServingStore.Gold)]
    [InlineData("Gold", ServingStore.Gold)]
    [InlineData("sqlite", ServingStore.Sqlite)]
    [InlineData("SQLITE", ServingStore.Sqlite)]
    [InlineData("Sqlite", ServingStore.Sqlite)]
    public void Parse_ReturnsExpectedMode(string? input, ServingStore expected)
    {
        Assert.Equal(expected, ServingStores.Parse(input));
    }

    [Theory]
    [InlineData("sqlite3")]
    [InlineData("postgres")]
    [InlineData("foo")]
    public void Parse_ThrowsOnInvalidValue(string input)
    {
        Assert.Throws<InvalidOperationException>(() => ServingStores.Parse(input));
    }
}

using EconData.Core;

namespace EconData.Tests;

public class ScaffoldTests
{
    [Fact]
    public void CoreAssemblyIsReferenced()
    {
        Assert.Equal("EconData.Core", CoreMarker.Name);
    }
}

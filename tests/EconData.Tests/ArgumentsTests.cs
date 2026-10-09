using EconData.Core.Cli;

namespace EconData.Tests;

public class ArgumentsTests
{
    [Fact]
    public void ParsesValuesFlagsAndEqualsForm()
    {
        var arguments = Arguments.Parse(new[] { "extract", "--metrics", "all", "--db", "--k=5" }, 1);

        Assert.Equal("all", arguments.Value("metrics"));
        Assert.True(arguments.Has("db"));
        Assert.Equal(5, arguments.IntValue("k", 0));
        Assert.False(arguments.Has("reembed"));
    }

    [Fact]
    public void FallsBackWhenMissingOrInvalid()
    {
        var arguments = Arguments.Parse(Array.Empty<string>(), 0);

        Assert.Equal("default", arguments.Value("metrics", "default"));
        Assert.Equal(9, arguments.IntValue("k", 9));
        Assert.False(arguments.Has("db"));
    }

    [Fact]
    public void CollectsPositionalArguments()
    {
        var arguments = Arguments.Parse(new[] { "chat", "extra", "--k", "2" }, 1);
        Assert.Equal(new[] { "extra" }, arguments.Positional);
        Assert.Equal(2, arguments.IntValue("k", 0));
    }
}

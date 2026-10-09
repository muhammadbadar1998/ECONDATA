using EconData.Core.Transform;

namespace EconData.Tests;

public class PeriodParserTests
{
    [Fact]
    public void ParsesAnnual()
    {
        var result = PeriodParser.Parse("2023");
        Assert.True(result.IsValid);
        Assert.Equal(new DateOnly(2023, 1, 1), result.Start);
        Assert.Equal(new DateOnly(2023, 12, 31), result.End);
        Assert.Equal("Annual", result.Frequency);
    }

    [Fact]
    public void ParsesQuarterlyYearFirstAndQuarterFirst()
    {
        var yearFirst = PeriodParser.Parse("2023-Q2");
        Assert.Equal(new DateOnly(2023, 4, 1), yearFirst.Start);
        Assert.Equal(new DateOnly(2023, 6, 30), yearFirst.End);
        Assert.Equal("Quarterly", yearFirst.Frequency);

        var quarterFirst = PeriodParser.Parse("Q3-2021");
        Assert.Equal(new DateOnly(2021, 7, 1), quarterFirst.Start);
        Assert.Equal(new DateOnly(2021, 9, 30), quarterFirst.End);
        Assert.Equal("Quarterly", quarterFirst.Frequency);
    }

    [Fact]
    public void ParsesMonthly()
    {
        var july = PeriodParser.Parse("2023-07");
        Assert.Equal(new DateOnly(2023, 7, 1), july.Start);
        Assert.Equal(new DateOnly(2023, 7, 31), july.End);
        Assert.Equal("Monthly", july.Frequency);

        var february = PeriodParser.Parse("2023-02");
        Assert.Equal(new DateOnly(2023, 2, 1), february.Start);
        Assert.Equal(new DateOnly(2023, 2, 28), february.End);
    }

    [Fact]
    public void ParsesDailyAsExactRange()
    {
        var result = PeriodParser.Parse("2023-07-15");
        Assert.Equal(new DateOnly(2023, 7, 15), result.Start);
        Assert.Equal(new DateOnly(2023, 7, 15), result.End);
        Assert.Equal("Monthly", result.Frequency);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2023-13")]
    public void InvalidLiteralsAreRejected(string? literal)
    {
        Assert.False(PeriodParser.Parse(literal).IsValid);
    }
}

using EconData.Core.Transform;

namespace EconData.Tests;

public class UnitNormalizerTests
{
    [Fact]
    public void EmptyExpressionIsIdentity()
    {
        Assert.Equal(5.0, UnitNormalizer.Normalize(5.0, string.Empty));
    }

    [Fact]
    public void MultipliesByMillion()
    {
        Assert.Equal(1e12, UnitNormalizer.Normalize(1e6, "x*1000000"));
    }

    [Fact]
    public void EvaluatesPrecedenceAndParentheses()
    {
        Assert.Equal(7.0, UnitNormalizer.Normalize(2.0, "(x+1)*2 + 1"));
    }

    [Fact]
    public void UnsafeExpressionThrows()
    {
        Assert.Throws<UnitNormalizationException>(() => UnitNormalizer.Normalize(1.0, "__import__('os')"));
    }

    [Theory]
    [InlineData("x**2")]
    [InlineData("x +")]
    [InlineData("x/0")]
    public void MalformedExpressionThrows(string expression)
    {
        Assert.Throws<UnitNormalizationException>(() => UnitNormalizer.Normalize(3.0, expression));
    }
}

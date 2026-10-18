using System.Text.Json;

using EconData.Core.Chat;

namespace EconData.Tests;

public class ChatToolValidatorTests
{
    [Fact]
    public void GetSeries_RequiresEntityAndMetric()
    {
        var valid = ChatToolValidator.Validate("get_series", Parse("{\"entity\":\"USA\",\"metric\":\"gdp_usd\"}"));
        Assert.True(valid.IsValid);
        Assert.Equal("get_series", valid.Call!.Name);

        var missingMetric = ChatToolValidator.Validate("get_series", Parse("{\"entity\":\"USA\"}"));
        Assert.False(missingMetric.IsValid);
        Assert.Contains("metric", missingMetric.Error);

        var missingEntity = ChatToolValidator.Validate("get_series", Parse("{\"metric\":\"gdp_usd\"}"));
        Assert.False(missingEntity.IsValid);
        Assert.Contains("entity", missingEntity.Error);
    }

    [Fact]
    public void CompareEntities_RequiresNonEmptyEntityArray()
    {
        var valid = ChatToolValidator.Validate("compare_entities", Parse("{\"metric\":\"gdp_usd\",\"entities\":[\"USA\",\"DEU\"]}"));
        Assert.True(valid.IsValid);

        var empty = ChatToolValidator.Validate("compare_entities", Parse("{\"metric\":\"gdp_usd\",\"entities\":[]}"));
        Assert.False(empty.IsValid);
        Assert.Contains("entities", empty.Error);

        var missing = ChatToolValidator.Validate("compare_entities", Parse("{\"metric\":\"gdp_usd\"}"));
        Assert.False(missing.IsValid);
    }

    [Fact]
    public void RankEntities_RequiresValidDirection()
    {
        var asc = ChatToolValidator.Validate("rank_entities", Parse("{\"metric\":\"population\",\"direction\":\"asc\"}"));
        Assert.True(asc.IsValid);

        var desc = ChatToolValidator.Validate("rank_entities", Parse("{\"metric\":\"population\",\"direction\":\"DESC\"}"));
        Assert.True(desc.IsValid);

        var bad = ChatToolValidator.Validate("rank_entities", Parse("{\"metric\":\"population\",\"direction\":\"sideways\"}"));
        Assert.False(bad.IsValid);
        Assert.Contains("direction", bad.Error);
    }

    [Fact]
    public void UnknownTool_IsInvalid()
    {
        var result = ChatToolValidator.Validate("no_such_tool", Parse("{}"));
        Assert.False(result.IsValid);
        Assert.Contains("no_such_tool", result.Error);
    }

    [Fact]
    public void ListTools_AreValidWithNoArgs()
    {
        foreach (var tool in new[] { "list_metrics", "list_entities", "get_latest" })
        {
            var result = ChatToolValidator.Validate(tool, Parse("{}"));
            Assert.True(result.IsValid, tool);
        }
    }

    [Fact]
    public void NonObjectArguments_FailGracefully()
    {
        // Some servers send arguments as a JSON string; the validator must not throw.
        var result = ChatToolValidator.Validate("get_series", Parse("\"{\\\"entity\\\":\\\"USA\\\"}\""));

        Assert.False(result.IsValid);
        Assert.Contains("JSON object", result.Error);
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json)!;
}

using System.Text.Json;

using EconData.Core.Ai;
using EconData.Core.Chat;
using EconData.Core.Configuration;

namespace EconData.Tests;

public class ChatToolsTests
{
    [Fact]
    public void EveryTool_HasASerializableParameterSchema()
    {
        foreach (var tool in ChatTools.All)
        {
            // An Undefined JsonElement (e.g. from a static-initializer ordering bug)
            // throws InvalidOperationException when serialized.
            var json = JsonSerializer.Serialize(tool.ParametersSchema);
            Assert.False(string.IsNullOrWhiteSpace(json));
            Assert.Equal(JsonValueKind.Object, tool.ParametersSchema.ValueKind);
        }
    }

    [Fact]
    public void ToolPayload_MatchesTheOpenAiFunctionShape()
    {
        var tools = ChatTools.All
            .Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.ParametersSchema,
                },
            })
            .ToList();

        var payload = JsonSerializer.SerializeToElement(tools);
        var text = payload.GetRawText();

        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        Assert.Contains("\"type\":\"function\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"get_series\"", text, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"entity\",\"metric\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LlmPayload_ExposesToolsAsATopLevelArray()
    {
        var config = new AppConfig();
        var client = new LLMClient(config, new HttpClient());
        var tools = ChatTools.All
            .Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = t.Name, ["parameters"] = t.ParametersSchema },
            })
            .ToList();
        var toolsElement = JsonSerializer.SerializeToElement(tools);

        var payload = client.BuildPayload(new[] { new ChatTurn("user", "hi") }, toolsElement);
        var serialized = JsonSerializer.Serialize(payload);

        Assert.Equal(JsonValueKind.Array, ((JsonElement)payload["tools"]!).ValueKind);
        Assert.Contains("\"tools\":[{", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\":{\"tools\"", serialized, StringComparison.Ordinal);
    }
}

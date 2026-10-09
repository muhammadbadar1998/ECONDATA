using EconData.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EconData.Tests;

public class AppConfigTests
{
    private static AppConfig FromSettings(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return AppConfig.FromConfiguration(configuration);
    }

    [Fact]
    public void UsesDefaultsWhenNothingConfigured()
    {
        var config = FromSettings(new Dictionary<string, string?>());

        Assert.Equal(AppConfig.DefaultSqlitePath, config.SqlitePath);
        Assert.Equal(AppConfig.DefaultOllamaHost, config.OllamaHost);
        Assert.Equal(AppConfig.DefaultOllamaEmbeddingModel, config.OllamaEmbeddingModel);
        Assert.Equal(60, config.OllamaTimeout);
        Assert.Equal(0.2, config.LlmTemperature);
        Assert.Equal(8, config.RagTopK);
        Assert.Equal("INFO", config.LogLevel);
    }

    [Fact]
    public void ValuesOverrideDefaultsAndAreNormalized()
    {
        var config = FromSettings(new Dictionary<string, string?>
        {
            ["ECONDATA_SQLITE_PATH"] = "C:/data/econ.db",
            ["OLLAMA_TIMEOUT"] = "99",
            ["LLM_TEMPERATURE"] = "0.7",
            ["LLM_API_KEY"] = "  key123  ",
            ["OLLAMA_HOST"] = "http://localhost:11434/",
            ["LOG_LEVEL"] = "debug",
        });

        Assert.Equal("C:/data/econ.db", config.SqlitePath);
        Assert.Equal(99, config.OllamaTimeout);
        Assert.Equal(0.7, config.LlmTemperature);
        Assert.Equal("key123", config.LlmApiKey);
        Assert.Equal("http://localhost:11434", config.OllamaHost);
        Assert.Equal("DEBUG", config.LogLevel);
    }

    [Fact]
    public void InvalidNumbersFallBackToDefaults()
    {
        var config = FromSettings(new Dictionary<string, string?> { ["OLLAMA_TIMEOUT"] = "not-a-number" });
        Assert.Equal(60, config.OllamaTimeout);
    }
}

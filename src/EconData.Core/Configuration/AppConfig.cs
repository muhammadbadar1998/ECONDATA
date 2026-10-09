using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace EconData.Core.Configuration;

/// <summary>
/// Strongly-typed application settings resolved from <c>config/appsettings.json</c>,
/// environment variables, and compiled defaults. Secrets are never required: the
/// SQLite database is a local file and the AI endpoint defaults to local Ollama.
/// </summary>
public sealed class AppConfig
{
    public const string DefaultSqlitePath = "./output/econdata.db";
    public const string DefaultOutputDir = "./output";
    public const string DefaultMetricsConfigPath = "./config/metrics.json";
    public const string DefaultEntitiesConfigPath = "./config/entities.json";
    public const string DefaultOllamaHost = "http://127.0.0.1:11434";
    public const string DefaultOllamaEmbeddingModel = "nomic-embed-text";
    public const string DefaultLlmBaseUrl = "http://127.0.0.1:11434/v1";
    public const string DefaultLlmModel = "qwen2.5:7b-instruct";

    public string SqlitePath { get; init; } = DefaultSqlitePath;
    public string OutputDir { get; init; } = DefaultOutputDir;
    public string MetricsConfigPath { get; init; } = DefaultMetricsConfigPath;
    public string EntitiesConfigPath { get; init; } = DefaultEntitiesConfigPath;
    public string OllamaHost { get; init; } = DefaultOllamaHost;
    public string OllamaEmbeddingModel { get; init; } = DefaultOllamaEmbeddingModel;
    public int OllamaTimeout { get; init; } = 60;
    public int OllamaEmbeddingBatchSize { get; init; } = 32;
    public string LlmBaseUrl { get; init; } = DefaultLlmBaseUrl;
    public string LlmApiKey { get; init; } = string.Empty;
    public string LlmModel { get; init; } = DefaultLlmModel;
    public int LlmTimeout { get; init; } = 120;
    public double LlmTemperature { get; init; } = 0.2;
    public int RagTopK { get; init; } = 8;
    public int RagRecentPoints { get; init; } = 5;
    public string LogLevel { get; init; } = "INFO";

    public static AppConfig FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new AppConfig
        {
            SqlitePath = configuration["ECONDATA_SQLITE_PATH"] ?? DefaultSqlitePath,
            OutputDir = configuration["OUTPUT_DIR"] ?? DefaultOutputDir,
            MetricsConfigPath = configuration["CONFIG_PATH"] ?? DefaultMetricsConfigPath,
            EntitiesConfigPath = configuration["ECONDATA_ENTITIES_PATH"] ?? DefaultEntitiesConfigPath,
            OllamaHost = (configuration["OLLAMA_HOST"] ?? DefaultOllamaHost).TrimEnd('/'),
            OllamaEmbeddingModel = configuration["OLLAMA_EMBEDDING_MODEL"] ?? DefaultOllamaEmbeddingModel,
            OllamaTimeout = GetInt(configuration, "OLLAMA_TIMEOUT", 60),
            OllamaEmbeddingBatchSize = GetInt(configuration, "OLLAMA_EMBEDDING_BATCH_SIZE", 32),
            LlmBaseUrl = (configuration["LLM_BASE_URL"] ?? DefaultLlmBaseUrl).TrimEnd('/'),
            LlmApiKey = (configuration["LLM_API_KEY"] ?? string.Empty).Trim(),
            LlmModel = configuration["LLM_MODEL"] ?? DefaultLlmModel,
            LlmTimeout = GetInt(configuration, "LLM_TIMEOUT", 120),
            LlmTemperature = GetDouble(configuration, "LLM_TEMPERATURE", 0.2),
            RagTopK = GetInt(configuration, "RAG_TOP_K", 8),
            RagRecentPoints = GetInt(configuration, "RAG_RECENT_POINTS", 5),
            LogLevel = (configuration["LOG_LEVEL"] ?? "INFO").ToUpperInvariant(),
        };
    }

    public static AppConfig Load(string? contentRoot = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot ?? Directory.GetCurrentDirectory())
            .AddJsonFile(Path.Combine("config", "appsettings.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        return FromConfiguration(configuration);
    }

    private static int GetInt(IConfiguration configuration, string key, int fallback)
        => int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double GetDouble(IConfiguration configuration, string key, double fallback)
        => double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

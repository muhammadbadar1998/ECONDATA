using EconData.Core.Ai;
using EconData.Core.Analytics;
using EconData.Core.Catalog;
using EconData.Core.Cli;
using EconData.Core.Cli.Chat;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Diagnostics;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using EconData.Core.Logging;
using EconData.Core.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace EconData.Cli;

internal static class Program
{
    private const string DefaultSchemaPath = "sql/sqlite/schema.sql";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = Arguments.Parse(args, 1);
        var root = CliPaths.Root();
        var config = BuildConfig(root, options);
        var basename = command == "chat" ? "chat" : "pipeline";
        var logger = LoggingSetup.Configure(config, basename: basename, console: true, consoleToStderr: command == "chat");

        try
        {
            return command switch
            {
                "init-db" => InitDb(config, root, options, logger),
                "check-db" => CheckDb(config, root, logger),
                "backup" => BackupDb(config, root, options, logger),
                "doctor" => await DoctorAsync(config, root, logger).ConfigureAwait(false),
                "extract" => await ExtractAsync(config, root, options, logger).ConfigureAwait(false),
                "lake" => await LakeAsync(config, root, options, logger).ConfigureAwait(false),
                "vectorize" => await VectorizeAsync(config, root, options, logger).ConfigureAwait(false),
                "chat" => await ChatAsync(config, root, options, logger).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Command '{Command}' failed.", command);
            return 1;
        }
    }

    private static int InitDb(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));
        var initializer = new SchemaInitializer(factory, logger);
        var ok = initializer.Initialize(CliPaths.Resolve(root, options.Value("schema", DefaultSchemaPath)!));
        if (ok)
        {
            TryCreateVectorTable(factory, config, root, logger);
        }

        Console.WriteLine(ok ? "Database schema initialized." : "Database schema initialization failed.");
        return ok ? 0 : 1;
    }

    private static int BackupDb(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var sourcePath = CliPaths.Resolve(root, config.SqlitePath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Database not found: {sourcePath}");
            return 1;
        }

        var destination = CliPaths.Resolve(root, options.Value("to", null) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(destination))
        {
            Console.Error.WriteLine("Usage: backup --to <path>");
            return 1;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Open the source read-only (no query_only pragma) so VACUUM INTO may write the
        // destination while the live database is never modified.
        var sourceConnection = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new SqliteConnection(sourceConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        // VACUUM INTO produces a compact, consistent snapshot without needing the sqlite3 CLI.
        command.CommandText = "VACUUM INTO @dest;";
        command.Parameters.AddWithValue("@dest", Path.GetFullPath(destination));
        command.ExecuteNonQuery();

        logger.Information("Backup written to {Destination}.", destination);
        Console.WriteLine($"Backup written to {destination}.");
        return 0;
    }

    private static int CheckDb(AppConfig config, string root, ILogger logger)
    {
        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));
        var initializer = new SchemaInitializer(factory, logger);
        var ok = initializer.Verify(out var message);
        Console.WriteLine(ok ? $"Database OK ({message})." : $"Database check failed: {message}");
        return ok ? 0 : 1;
    }

    private static async Task<int> DoctorAsync(AppConfig config, string root, ILogger logger)
    {
        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));

        using var http = new HttpClient();
        var embedder = new OllamaEmbedder(config, http, logger);
        var llm = new LLMClient(config, http, logger);

        var service = new DiagnosticsService(
            config,
            factory,
            logger,
            embedder,
            ct => llm.VerifyConnectivityAsync(ct));

        var report = await service.RunAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var check in report.Checks)
        {
            Console.WriteLine($"{check.Diagnosis.ToString().ToUpperInvariant(),-7} {check.Name,-10} {check.Detail}");
        }

        var pass = report.Checks.Count(c => c.Diagnosis == Diagnosis.Pass);
        var warn = report.Checks.Count(c => c.Diagnosis == Diagnosis.Warn);
        var block = report.Checks.Count(c => c.Diagnosis == Diagnosis.Blocker);
        Console.WriteLine($"Summary: {pass} pass, {warn} warn, {block} blocker.");

        return report.HasBlockers ? 1 : 0;
    }

    /// <summary>Builds the tier plan from <c>--bronze/--silver/--gold</c>; null keeps legacy behaviour.</summary>
    private static PipelineTiers? ResolveTiers(Arguments options)
    {
        var bronze = options.Has("bronze");
        var silver = options.Has("silver");
        var gold = options.Has("gold") || options.Has("db");
        return bronze || silver || gold ? new PipelineTiers(bronze, silver, gold) : null;
    }

    private static async Task<int> LakeAsync(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var action = options.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var lake = LakePaths.Resolve(root, config.LakeDir);
        var gold = new GoldTier(lake.GoldPath, CliPaths.Resolve(root, "sql/duckdb/schema.sql"), logger);

        switch (action)
        {
            case "init":
                {
                    var initLake = LakePaths.Resolve(root, config.LakeDir);
                    initLake.EnsureDirectories();
                    var initGold = new GoldTier(initLake.GoldPath, CliPaths.Resolve(root, "sql/duckdb/schema.sql"), logger);
                    initGold.EnsureSchema();
                    Console.WriteLine($"Lake initialized at {initLake.LakeDir}.");
                    return 0;
                }

            case "status":
                {
                    var status = await new LakeStatusService(lake, gold, logger).GetAsync().ConfigureAwait(false);
                    Console.WriteLine($"Lake: {status.Directory}");
                    foreach (var tier in new[] { status.Bronze, status.Silver, status.Gold })
                    {
                        Console.WriteLine(
                            $"  {tier.Tier,-7} present={tier.Present,-5} rows={tier.Rows,-8} files={tier.Files,-4} bytes={tier.Bytes,-10} newest={tier.Newest ?? "-"} detail={tier.Detail}");
                        foreach (var (name, rows) in tier.Tier == "gold" ? status.Tables : status.Partitions)
                        {
                            Console.WriteLine($"          {name} = {rows}");
                        }
                    }

                    return 0;
                }

            case "build-gold":
                {
                    var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
                    var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));
                    gold.EnsureSchema();
                    var silver = new SilverTier(lake, logger);
                    var partitions = silver.Partitions();
                    if (partitions.Count == 0)
                    {
                        Console.WriteLine("No silver partitions found. Run `extract --silver` first.");
                        return 1;
                    }

                    gold.SeedEntities(seed.Entities, seed.Aliases);
                    gold.SeedMemberships(seed.Memberships);
                    var records = EconData.Core.Transform.DataTransformer.CreateMetricsRecords(catalog);
                    gold.UpsertMetrics(records);
                    var (metricIdsBeforeSources, _) = gold.GetMetricAndSourceIds();
                    gold.UpsertSources(records, metricIdsBeforeSources);
                    var (metricIds, sourceIds) = gold.GetMetricAndSourceIds();

                    var total = 0;
                    foreach (var code in partitions.Where(metricIds.ContainsKey))
                    {
                        var rows = await silver.ReadAsync(code).ConfigureAwait(false);
                        total += gold.UpsertObservations(code, rows, metricIds[code], sourceIds[code]);
                    }

                    foreach (var code in partitions.Where(metricIds.ContainsKey))
                    {
                        var metric = catalog.Get(code);
                        if (metric is not null && metric.Aggregation is "sum" or "mean")
                        {
                            gold.Aggregate(code, metric.Aggregation, metric.EffectiveBaseUnit, metricIds);
                        }
                    }

                    gold.RecordRun("econdata_manual", "build_gold", partitions.Count, total, 0, new List<string>(), sourceIds.Values.ToList(), "SUCCESS", 0, "G");
                    Console.WriteLine($"Gold rebuilt from {partitions.Count} silver partitions: {total} observations.");
                    return 0;
                }

            case "rebuild":
                {
                    var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
                    var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));
                    var rebuildPath = Path.Combine(lake.GoldDir, "econdata.rebuild.duckdb");
                    if (File.Exists(rebuildPath)) File.Delete(rebuildPath);
                    var rebuild = new GoldTier(rebuildPath, CliPaths.Resolve(root, "sql/duckdb/schema.sql"), logger);
                    var silver = new SilverTier(lake, logger);
                    var partitions = silver.Partitions();
                    if (partitions.Count == 0)
                    {
                        Console.WriteLine("No silver partitions found. Run `extract --silver` first.");
                        return 1;
                    }

                    var counts = await rebuild.RebuildFromSilverAsync(seed, catalog, silver, partitions).ConfigureAwait(false);

                    // Validate: per-metric gold rows must match silver rows before we swap.
                    var silverCounts = await silver.RowCountsAsync().ConfigureAwait(false);
                    var mismatches = new List<string>();
                    foreach (var (code, goldRows) in counts)
                    {
                        if (!silverCounts.TryGetValue(code, out var silverRows) || silverRows != goldRows)
                        {
                            mismatches.Add(code);
                        }
                    }
                    foreach (var code in partitions.Where(c => !counts.ContainsKey(c)))
                    {
                        mismatches.Add(code);
                    }

                    if (mismatches.Count > 0)
                    {
                        Console.Error.WriteLine("Rebuild validation failed; not swapping. Mismatched metrics: "
                            + string.Join(", ", mismatches));
                        return 1;
                    }

                    var total = counts.Values.Sum(v => (int)v);
                    File.Move(rebuildPath, lake.GoldPath, overwrite: true);
                    Console.WriteLine($"Gold rebuilt from {counts.Count} silver partitions: {total} observations, swapped atomically.");
                    return 0;
                }

            case "import-from-sqlite":
                {
                    var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
                    var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));
                    var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));
                    gold.EnsureSchema();
                    gold.SeedEntities(seed.Entities, seed.Aliases);
                    gold.SeedMemberships(seed.Memberships);
                    gold.ImportFromSqlite(factory);
                    Console.WriteLine($"Gold imported from SQLite at {factory.DatabasePath}.");
                    return 0;
                }

            default:
                Console.WriteLine("Usage: lake [status|build-gold|build-gold --rebuild|import-from-sqlite]");
                return 1;
        }
    }

    private static async Task<int> ExtractAsync(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
        var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));

        var metrics = ResolveMetrics(options.Value("metrics", "all")!, catalog);
        if (metrics.Count == 0)
        {
            Console.WriteLine($"No valid metric codes specified. Available: {string.Join(", ", catalog.Codes)}");
            return 1;
        }

        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));
        using var http = new HttpClient();
        var source = new DBnomicsExtractor(http, config.DbNomicsBaseUrl, logger: logger);
        var lake = LakePaths.Resolve(root, config.LakeDir);
        lake.EnsureDirectories();
        var runner = new PipelineRunner(
            catalog,
            seed,
            factory,
            CliPaths.Resolve(root, options.Value("schema", DefaultSchemaPath)!),
            source,
            logger,
            lake: lake);

        var tiers = ResolveTiers(options);
        var summary = tiers is null
            ? await runner.RunAsync(metrics, options.Has("db")).ConfigureAwait(false)
            : await runner.RunAsync(metrics, options.Has("db"), tiers).ConfigureAwait(false);
        Console.WriteLine(
            $"Extraction complete: {summary.TotalObservations} observations, {summary.ObservationsUpserted} upserted in {summary.ExecutionSeconds:F2}s.");
        if (summary.FailedMetrics.Count > 0)
        {
            Console.Error.WriteLine($"Failed metrics: {string.Join(", ", summary.FailedMetrics)}");
            return 1;
        }
        return 0;
    }

    private static async Task<int> VectorizeAsync(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
        var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));
        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));

        new SchemaInitializer(factory, logger).Initialize(CliPaths.Resolve(root, options.Value("schema", DefaultSchemaPath)!));
        var store = new VectorStore(factory, CliPaths.Resolve(root, config.SqliteVecExtensionPath), logger);

        var lake = LakePaths.Resolve(root, config.LakeDir);
        lake.EnsureDirectories();
        var gold = new GoldTier(lake.GoldPath, CliPaths.Resolve(root, "sql/duckdb/schema.sql"), logger);

        using var http = new HttpClient();
        var embedder = new OllamaEmbedder(config, http, logger);
        var vectorizer = new Vectorizer(catalog, seed, factory, store, embedder, options.Has("reembed"), logger,
            gold);
        var stats = await vectorizer.VectorizeAllAsync(options.IntValue("recent-points", Vectorizer.DefaultRecentPoints)).ConfigureAwait(false);

        Console.WriteLine(
            $"Vectorization complete: embedded={stats["embedded"]} updated={stats["updated"]} " +
            $"orphans_removed={stats["orphans_removed"]} errors={stats["errors"]}.");
        return stats["errors"] > 0 ? 1 : 0;
    }

    private static async Task<int> ChatAsync(AppConfig config, string root, Arguments options, ILogger logger)
    {
        var catalog = MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath));
        var seed = EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath));
        var factory = new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath));
        var store = new VectorStore(factory, CliPaths.Resolve(root, config.SqliteVecExtensionPath), logger);

        var lake = LakePaths.Resolve(root, config.LakeDir);
        lake.EnsureDirectories();
        var gold = new GoldTier(lake.GoldPath, CliPaths.Resolve(root, "sql/duckdb/schema.sql"), logger);

        using var http = new HttpClient();
        var embedder = new OllamaEmbedder(config, http, logger);
        var llm = new LLMClient(config, http, logger);
        var vectorizer = new Vectorizer(catalog, seed, factory, store, embedder, forceReembed: false, logger,
            gold);
        var k = options.IntValue("k", config.RagTopK);

        IRagAnswerer AnswererFactory(string systemPrompt) => new RagAnswerer(vectorizer, llm, k, systemPrompt, logger);

        var chatOptions = new ChatOptions(
            K: k,
            ShowSources: options.Has("show-sources"),
            SystemPromptPath: options.Value("system", string.Empty)!,
            Question: options.Value("question", string.Empty)!);

        return await ChatCommand.RunAsync(
            chatOptions,
            AnswererFactory,
            (_, cancellationToken) => ChatCommand.PreflightAsync(embedder, llm, cancellationToken),
            Console.In,
            Console.Out,
            Console.Error).ConfigureAwait(false);
    }

    private static void TryCreateVectorTable(SqliteConnectionFactory factory, AppConfig config, string root, ILogger logger)
    {
        try
        {
            new VectorStore(factory, CliPaths.Resolve(root, config.SqliteVecExtensionPath), logger).EnsureVectorTable();
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or BadImageFormatException)
        {
            logger.Warning("Could not create the sqlite-vec table ({Message}). Run `vectorize` once the extension is available.", ex.Message);
        }
    }

    private static List<string> ResolveMetrics(string metricsOption, MetricsCatalog catalog)
    {
        if (string.Equals(metricsOption, "all", StringComparison.OrdinalIgnoreCase))
        {
            return catalog.Codes.ToList();
        }

        return metricsOption
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(code => catalog.Get(code) is not null)
            .ToList();
    }

    private static AppConfig BuildConfig(string root, Arguments options)
    {
        var overrides = new Dictionary<string, string?>();
        SetOverride(overrides, "CONFIG_PATH", root, options.Value("config"));
        SetOverride(overrides, "OUTPUT_DIR", root, options.Value("output-dir"));
        SetOverride(overrides, "ECONDATA_SQLITE_PATH", root, options.Value("db-path"));
        SetOverride(overrides, "ECONDATA_ENTITIES_PATH", root, options.Value("entities"));
        SetOverride(overrides, "ECONDATA_SQLITE_VEC_PATH", root, options.Value("vec"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile(Path.Combine("config", "appsettings.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(overrides)
            .Build();
        return AppConfig.FromConfiguration(configuration);
    }

    private static void SetOverride(IDictionary<string, string?> overrides, string key, string root, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            overrides[key] = CliPaths.Resolve(root, value);
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("econdata <command> [options]");
        Console.WriteLine();
        Console.WriteLine("  init-db                          Create the SQLite dbe_* schema and exit");
        Console.WriteLine("  check-db                         Verify the SQLite schema integrity and exit");
        Console.WriteLine("  doctor                           Readiness check (SDK, DB, schema, sqlite-vec, Ollama, LLM)");
        Console.WriteLine("  lake init                        Idempotently initialize the lake (gold schema + directories)");
        Console.WriteLine("  lake status                      Show bronze/silver/gold tier state");
        Console.WriteLine("  lake build-gold                  Rebuild gold from silver partitions");
        Console.WriteLine("  extract [--metrics all|a,b] [--db]   Run the extract/transform/load pipeline");
        Console.WriteLine("  vectorize [--reembed] [--recent-points N]   Build/refresh the semantic catalog");
        Console.WriteLine("  chat [--question Q] [--k N] [--show-sources] [--system FILE]   Ask the catalog");
        Console.WriteLine();
        Console.WriteLine("Common options: --config PATH --db-path PATH --entities PATH --vec PATH --output-dir DIR --schema PATH");
        Console.WriteLine($"Defaults: config={AppConfig.DefaultMetricsConfigPath}, db={AppConfig.DefaultSqlitePath}, output={AppConfig.DefaultOutputDir}");
    }
}

using System.Globalization;
using EconData.Core.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EconData.Core.Logging;

public static class LoggingSetup
{
    public const string LogTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const long MaxLatestBytes = 5 * 1024 * 1024;
    private const int LatestBackupCount = 5;

    public static ILogger Configure(AppConfig config, string basename = "pipeline", bool console = true, bool consoleToStderr = false, ILogEventSink? extraSink = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Configure(config.OutputDir, basename, ParseLevel(config.LogLevel), console, consoleToStderr, extraSink);
    }

    public static ILogger Configure(
        string outputDir,
        string basename = "pipeline",
        LogEventLevel level = LogEventLevel.Information,
        bool console = true,
        bool consoleToStderr = false,
        ILogEventSink? extraSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(basename);

        var logsDir = Path.Combine(outputDir, "logs");
        Directory.CreateDirectory(logsDir);
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var datedPath = Path.Combine(logsDir, $"{basename}_{date}.log");
        var latestPath = Path.Combine(outputDir, $"{basename}.log");

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("SourceContext", typeof(LoggingSetup).Namespace)
            .WriteTo.File(datedPath, outputTemplate: LogTemplate)
            .WriteTo.File(
                latestPath,
                outputTemplate: LogTemplate,
                fileSizeLimitBytes: MaxLatestBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: LatestBackupCount);

        if (extraSink is not null)
        {
            configuration = configuration.WriteTo.Sink(extraSink);
        }

        if (console)
        {
            configuration = consoleToStderr
                ? configuration.WriteTo.Console(outputTemplate: LogTemplate, standardErrorFromLevel: LogEventLevel.Verbose)
                : configuration.WriteTo.Console(outputTemplate: LogTemplate);
        }

        return configuration.CreateLogger();
    }

    public static LogEventLevel ParseLevel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.ToUpperInvariant() switch
        {
            "VERBOSE" or "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "WARN" or "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" or "CRITICAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };
    }
}

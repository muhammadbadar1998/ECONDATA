using EconData.Core.Logging;
using Serilog;
using Serilog.Events;

namespace EconData.Tests;

public class LoggingSetupTests
{
    [Fact]
    public void WritesRecordsToDatedAndLatestFiles()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "econdata-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        try
        {
            var logger = LoggingSetup.Configure(outputDir, "pipeline", LogEventLevel.Information, console: false);
            logger.ForContext("SourceContext", "src.extractor").Information("PROBE-log-12345");
            (logger as IDisposable)?.Dispose();

            var dated = Path.Combine(outputDir, "logs", $"pipeline_{DateTime.UtcNow:yyyy-MM-dd}.log");
            var latest = Path.Combine(outputDir, "pipeline.log");

            Assert.True(File.Exists(dated), $"missing dated log: {dated}");
            Assert.True(File.Exists(latest), $"missing latest log: {latest}");
            Assert.Contains("PROBE-log-12345", File.ReadAllText(dated));
            Assert.Contains("PROBE-log-12345", File.ReadAllText(latest));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void ParseLevelMapsPythonStyleNames()
    {
        Assert.Equal(LogEventLevel.Debug, LoggingSetup.ParseLevel("DEBUG"));
        Assert.Equal(LogEventLevel.Warning, LoggingSetup.ParseLevel("WARN"));
        Assert.Equal(LogEventLevel.Warning, LoggingSetup.ParseLevel("warning"));
        Assert.Equal(LogEventLevel.Information, LoggingSetup.ParseLevel("nonsense"));
    }
}

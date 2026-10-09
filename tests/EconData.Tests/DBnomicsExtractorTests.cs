using System.Net;
using System.Text;
using EconData.Core.Configuration;
using EconData.Core.Extraction;

namespace EconData.Tests;

public class DBnomicsExtractorTests
{
    private const string CannedJson = """
        {
          "series": {
            "num_found": 2,
            "docs": [
              {"dimensions": {"country": "USA"}, "period": ["2023", "2024"], "value": [1.0, 2.0], "series_name": "US"},
              {"dimensions": {"country": "DEU"}, "period": ["2023"], "value": [3.0], "series_name": "DE"}
            ]
          }
        }
        """;

    [Fact]
    public async Task ParsesSeriesDocsFromASinglePage()
    {
        var handler = new StubHandler(CannedJson);
        using var http = new HttpClient(handler);
        var extractor = new DBnomicsExtractor(http);
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));

        var series = await extractor.FetchMetricSeriesAsync("gdp_usd", catalog);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, series.Count);
        Assert.Equal("USA", series[0].Dimensions["country"]);
        Assert.Equal(new[] { "2023", "2024" }, series[0].Periods);
        Assert.Equal(1.0, double.Parse(series[0].Values[0]!));
        Assert.Equal("DE", series[1].SeriesName);
    }

    [Fact]
    public async Task UnknownMetricThrows()
    {
        using var http = new HttpClient(new StubHandler(CannedJson));
        var extractor = new DBnomicsExtractor(http);
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => extractor.FetchMetricSeriesAsync("nope", catalog));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

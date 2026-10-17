using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EconData.Web.Services;

namespace EconData.Web.Tests;

public class JobApiTests : IClassFixture<JobsWebAppFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly JobsWebAppFixture _fixture;
    private readonly HttpClient _client;

    public JobApiTests(JobsWebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostJob_SecondPostWhileRunning_ReturnsConflict_AndCancelTransitions()
    {
        _fixture.Executor.Behavior = FakeBehavior.BlockUntilCancelled;

        var response = await _client.PostAsJsonAsync("/api/jobs", new JobRequest(JobKind.Check, null, true, false));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = accepted.GetProperty("job_id").GetString();
        Assert.False(string.IsNullOrEmpty(jobId));

        var second = await _client.PostAsJsonAsync("/api/jobs", new JobRequest(JobKind.Init, null, true, false));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var conflict = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(conflict.GetProperty("busy").GetBoolean());

        var current = await _client.GetFromJsonAsync<JobStatus>("/api/jobs/current");
        Assert.NotNull(current);
        Assert.Equal(JobState.Running, current!.State);
        Assert.Equal(jobId, current.JobId);

        var cancel = await _client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var terminal = await WaitForTerminalAsync();
        Assert.Equal(JobState.Cancelled, terminal.State);
    }

    [Fact]
    public async Task PostJob_Succeeds_AndLogContainsFakeExecutorLine()
    {
        _fixture.Executor.Behavior = FakeBehavior.Succeed;

        var response = await _client.PostAsJsonAsync("/api/jobs", new JobRequest(JobKind.Vectorize, null, true, false));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = accepted.GetProperty("job_id").GetString();

        var terminal = await WaitForTerminalAsync();
        Assert.Equal(JobState.Succeeded, terminal.State);
        Assert.Equal(JobKind.Vectorize, _fixture.Executor.LastRequest?.Kind);

        var logs = await _client.GetFromJsonAsync<IReadOnlyList<LogEntry>>($"/api/jobs/{jobId}/log?since=0");
        Assert.NotNull(logs);
        Assert.Contains(logs!, entry => entry.Message.Contains("Fake job executor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownJobId_LogIsEmpty()
    {
        var logs = await _client.GetFromJsonAsync<IReadOnlyList<LogEntry>>("/api/jobs/unknown/log?since=0");

        Assert.NotNull(logs);
        Assert.Empty(logs!);
    }

    [Fact]
    public async Task GetCatalogMetrics_ReturnsNineEntries()
    {
        var metrics = await _client.GetFromJsonAsync<IReadOnlyList<MetricItem>>("/api/catalog/metrics");

        Assert.NotNull(metrics);
        Assert.Equal(9, metrics!.Count);
        Assert.Contains(metrics, metric => metric.Code == "gdp_usd");
    }

    private async Task<JobStatus> WaitForTerminalAsync()
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await _client.GetFromJsonAsync<JobStatus>("/api/jobs/current");
            if (status is not null && status.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
            {
                return status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Job did not reach a terminal state.");
    }
}

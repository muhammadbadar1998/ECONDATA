using System.Net;
using System.Net.Http.Json;
using EconData.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// The Runner page posts its tier checkboxes as <c>bronze</c>/<c>silver</c>/<c>gold</c>.
/// This test posts that exact payload to the real endpoint and asserts the flags survive
/// JSON binding into <see cref="JobRequest"/>, which is what <c>PipelineJobExecutor</c>
/// turns into <c>PipelineTiers</c>.
/// </summary>
public class JobRequestTierFlagsTests
{
    [Fact]
    public async Task PostJob_TierFlagsReachTheExecutor()
    {
        var executor = new CapturingExecutor();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJobExecutor>();
                services.AddSingleton<IJobExecutor>(executor);
            }));

        using var client = factory.CreateClient();
        var payload = new
        {
            kind = (int)JobKind.Extract,
            metrics = new[] { "gdp_usd" },
            load_to_db = false,
            reembed = false,
            bronze = true,
            silver = true,
            gold = true,
        };

        var response = await client.PostAsJsonAsync("/api/jobs", payload);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var request = await executor.Captured.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobKind.Extract, request.Kind);
        Assert.False(request.LoadToDb);
        Assert.True(request.Bronze);
        Assert.True(request.Silver);
        Assert.True(request.Gold);
    }

    private sealed class CapturingExecutor : IJobExecutor
    {
        private readonly TaskCompletionSource<JobRequest> _captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JobRequest> Captured => _captured.Task;

        public Task<JobResult> ExecuteAsync(
            JobRequest request,
            IProgress<JobProgressUpdate> progress,
            CancellationToken cancellationToken)
        {
            _captured.TrySetResult(request);
            return Task.FromResult(new JobResult("captured"));
        }
    }
}

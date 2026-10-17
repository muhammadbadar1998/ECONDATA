using EconData.Web.Services;

namespace EconData.Web.Tests;

public class JobServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TryStart_RunsToSuccess()
    {
        var executor = new FakeJobExecutor { Behavior = FakeBehavior.Succeed };
        var service = new JobService(executor);
        var request = new JobRequest(JobKind.Check, null, true, false);

        var (started, jobId) = service.TryStart(request);

        Assert.True(started);
        Assert.False(string.IsNullOrEmpty(jobId));
        var status = await WaitForTerminalAsync(service);
        Assert.Equal(JobState.Succeeded, status.State);
        Assert.Equal(jobId, status.JobId);
        Assert.Equal(JobKind.Check, status.Kind);
        Assert.Equal(request, executor.LastRequest);
    }

    [Fact]
    public async Task TryStart_WhileRunning_IsRejected()
    {
        var executor = new FakeJobExecutor { Behavior = FakeBehavior.BlockUntilCancelled };
        var service = new JobService(executor);

        var (started, jobId) = service.TryStart(new JobRequest(JobKind.Check, null, true, false));
        Assert.True(started);
        await executor.Started.Task.WaitAsync(Timeout);

        var (secondStarted, secondId) = service.TryStart(new JobRequest(JobKind.Init, null, true, false));
        Assert.False(secondStarted);
        Assert.Equal(jobId, secondId);
        Assert.Equal(JobState.Running, service.Snapshot().State);

        Assert.True(service.Cancel(jobId));
        var status = await WaitForTerminalAsync(service);
        Assert.Equal(JobState.Cancelled, status.State);
    }

    [Fact]
    public async Task ExecutorFailure_TransitionsToFailed()
    {
        var executor = new FakeJobExecutor { Behavior = FakeBehavior.Throw };
        var service = new JobService(executor);

        service.TryStart(new JobRequest(JobKind.Check, null, true, false));

        var status = await WaitForTerminalAsync(service);
        Assert.Equal(JobState.Failed, status.State);
        Assert.Equal("fake failure", status.Error);
    }

    [Fact]
    public async Task Progress_IsReflectedInSnapshot()
    {
        var executor = new FakeJobExecutor { Behavior = FakeBehavior.EmitProgress };
        var service = new JobService(executor);

        service.TryStart(new JobRequest(JobKind.Vectorize, null, true, false));

        var status = await WaitForTerminalAsync(service);
        Assert.Equal(JobState.Succeeded, status.State);
        Assert.Equal("test", status.Phase);
        Assert.Equal(5, status.Current);
        Assert.Equal(10, status.Total);
    }

    [Fact]
    public void Cancel_UnknownJob_ReturnsFalse()
    {
        var service = new JobService(new FakeJobExecutor());

        Assert.False(service.Cancel("missing"));
    }

    private static async Task<JobStatus> WaitForTerminalAsync(JobService service)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = service.Snapshot();
            if (status.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)
            {
                return status;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Job did not finish: {service.Snapshot().State}");
    }
}

using EconData.Web.Services;
using Serilog;

namespace EconData.Web.Tests;

public enum FakeBehavior
{
    Succeed,
    Throw,
    BlockUntilCancelled,
    EmitProgress
}

public sealed class FakeJobExecutor : IJobExecutor
{
    private readonly ILogger _logger;

    public FakeJobExecutor(ILogger? logger = null)
    {
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    public FakeBehavior Behavior { get; set; } = FakeBehavior.Succeed;

    public JobRequest? LastRequest { get; private set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<JobResult> ExecuteAsync(
        JobRequest request,
        IProgress<JobProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        _logger.Information("Fake job executor running {Kind}.", request.Kind);
        Started.TrySetResult();

        return Behavior switch
        {
            FakeBehavior.Succeed => Task.FromResult(new JobResult("fake success")),
            FakeBehavior.Throw => Task.FromException<JobResult>(new InvalidOperationException("fake failure")),
            FakeBehavior.BlockUntilCancelled => BlockUntilCancelledAsync(cancellationToken),
            FakeBehavior.EmitProgress => EmitProgressAsync(progress),
            _ => Task.FromResult(new JobResult("fake success"))
        };
    }

    private async Task<JobResult> BlockUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken), Release.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new JobResult("fake released");
    }

    private static Task<JobResult> EmitProgressAsync(IProgress<JobProgressUpdate> progress)
    {
        progress.Report(new JobProgressUpdate("test", "half done", 5, 10));
        return Task.FromResult(new JobResult("fake progress"));
    }
}

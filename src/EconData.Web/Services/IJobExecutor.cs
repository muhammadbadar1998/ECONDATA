namespace EconData.Web.Services;

public interface IJobExecutor
{
    Task<JobResult> ExecuteAsync(
        JobRequest request,
        IProgress<JobProgressUpdate> progress,
        CancellationToken cancellationToken);
}

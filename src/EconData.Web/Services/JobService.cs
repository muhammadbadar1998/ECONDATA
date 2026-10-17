namespace EconData.Web.Services;

public sealed class JobService
{
    private readonly IJobExecutor _executor;
    private readonly object _lock = new();
    private string? _jobId;
    private JobKind _kind;
    private JobState _state = JobState.Idle;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private string? _error;
    private string? _phase;
    private string? _message;
    private int _current;
    private int _total;
    private readonly List<StepLine> _steps = new();
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private long _counter;

    public JobService(IJobExecutor executor)
    {
        _executor = executor;
    }

    public (bool started, string jobId) TryStart(JobRequest request)
    {
        lock (_lock)
        {
            if (_state == JobState.Running)
            {
                return (false, _jobId ?? string.Empty);
            }
            var id = $"job_{Interlocked.Increment(ref _counter)}";
            _jobId = id;
            _kind = request.Kind;
            _state = JobState.Running;
            _startedAt = DateTimeOffset.UtcNow;
            _finishedAt = null;
            _error = null;
            _phase = "starting";
            _message = "Starting job";
            _current = 0;
            _total = 0;
            _steps.Clear();
            _cts = new CancellationTokenSource();
            var progress = new Progress<JobProgressUpdate>(update =>
            {
                lock (_lock)
                {
                    _phase = update.Phase;
                    _message = update.Message;
                    _current = update.Current;
                    _total = update.Total;
                    if (!string.IsNullOrEmpty(update.MetricCode))
                    {
                        var line = new StepLine(
                            update.Current,
                            update.MetricCode,
                            update.Records,
                            update.Upserted,
                            update.Seconds,
                            update.StepState ?? "running");
                        var index = _steps.FindIndex(step => step.Index == update.Current);
                        if (index >= 0)
                        {
                            _steps[index] = line;
                        }
                        else
                        {
                            _steps.Add(line);
                        }
                    }
                }
            });
            _runningTask = Task.Run(async () =>
            {
                try
                {
                    var result = await _executor.ExecuteAsync(request, progress, _cts.Token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_state == JobState.Running)
                        {
                            _state = JobState.Succeeded;
                            _finishedAt = DateTimeOffset.UtcNow;
                            _message = result.Message;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_lock)
                    {
                        _state = JobState.Cancelled;
                        _finishedAt = DateTimeOffset.UtcNow;
                        _error = "Cancelled";
                    }
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        _state = JobState.Failed;
                        _finishedAt = DateTimeOffset.UtcNow;
                        _error = ex.Message;
                    }
                }
            }, _cts.Token);
            return (true, id);
        }
    }

    public JobStatus Snapshot()
    {
        lock (_lock)
        {
            var stepsCopy = _steps.ToList();
            return new JobStatus(
                _jobId,
                _kind,
                _state,
                _startedAt,
                _finishedAt,
                _error,
                _phase,
                _message,
                _current,
                _total,
                stepsCopy);
        }
    }

    public bool Cancel(string jobId)
    {
        lock (_lock)
        {
            if (_state != JobState.Running || _jobId != jobId)
            {
                return false;
            }
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }
            return true;
        }
    }
}

using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace EconData.Web.Services;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly int _capacity = 500;
    private long _sequence;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage());
        _queue.Enqueue(entry);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
            // Trim oldest
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(long sinceSequence)
    {
        var list = new List<LogEntry>(_queue.Count);
        foreach (var entry in _queue)
        {
            if (entry.Sequence > sinceSequence)
            {
                list.Add(entry);
            }
        }
        return list;
    }
}

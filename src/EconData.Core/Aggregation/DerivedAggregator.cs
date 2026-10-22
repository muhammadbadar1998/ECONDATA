using EconData.Core.Configuration;
using EconData.Core.Data;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Core.Aggregation;

public sealed record DerivedResult(int RowsWritten, IReadOnlyList<string> Metrics);

public sealed class DerivedAggregator
{
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseLoader _loader;
    private readonly ILogger _logger;

    public DerivedAggregator(SqliteConnectionFactory factory, DatabaseLoader loader, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _loader = loader;
        _logger = logger;
    }

    public DerivedResult Aggregate(
        IReadOnlyList<string> metricCodes,
        IReadOnlyList<MetricRecord> metrics,
        IReadOnlyDictionary<string, int> metricIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricCodes);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(metricIds);

        var metricByCode = metrics.ToDictionary(m => m.MetricCode, StringComparer.Ordinal);
        var totalRows = 0;
        var done = new List<string>();

        using var connection = _factory.Open();
        var memberships = LoadMemberships(connection);

        foreach (var code in metricCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metricByCode.TryGetValue(code, out var m) || !metricIds.TryGetValue(code, out var metricId))
            {
                continue;
            }

            if (!string.Equals(m.AggregationMethod, "sum", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(m.AggregationMethod, "mean", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var method = m.AggregationMethod.ToLowerInvariant();
            var rows = Compute(metricId, method, m.BaseUnit, memberships, connection);
            var written = _loader.UpsertDerivedObservations(metricId, rows);
            totalRows += written;
            done.Add(code);
            _logger.Information("Derived aggregation for '{Metric}': {Rows} rows.", code, written);
        }

        return new DerivedResult(totalRows, done);
    }

    private static List<object?[]> Compute(
        int metricId,
        string method,
        string unit,
        IReadOnlyDictionary<int, IReadOnlyList<int>> memberships,
        SqliteConnection connection)
    {
        var rows = new List<object?[]>();

        // members that have ANY observation for this metric
        var membersWithData = new HashSet<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT entity_id FROM dbe_observations WHERE metric_id = @m";
            cmd.Parameters.AddWithValue("@m", metricId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                membersWithData.Add(r.GetInt32(0));
            }
        }

        if (membersWithData.Count == 0)
        {
            return rows;
        }

        foreach (var (aggregateId, memberIds) in memberships)
        {
            var contributingMembers = memberIds.Where(membersWithData.Contains).ToList();
            if (contributingMembers.Count == 0)
            {
                continue;
            }

            // group observations by (start_date, end_date) across contributing members
            var byPeriod = new Dictionary<(string Start, string End), List<(int EntityId, double Val)>>();
            foreach (var memberId in contributingMembers)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT start_date, end_date, val FROM dbe_observations WHERE metric_id = @m AND entity_id = @e";
                cmd.Parameters.AddWithValue("@m", metricId);
                cmd.Parameters.AddWithValue("@e", memberId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var sd = r.GetString(0);
                    var ed = r.GetString(1);
                    var val = r.GetDouble(2);
                    var key = (sd, ed);
                    if (!byPeriod.TryGetValue(key, out var list))
                    {
                        list = new List<(int, double)>();
                        byPeriod[key] = list;
                    }
                    list.Add((memberId, val));
                }
            }

            foreach (var (period, vals) in byPeriod)
            {
                var covered = vals.Select(v => v.EntityId).Distinct().Count();
                if (covered * 2 < contributingMembers.Count)
                {
                    continue;
                }

                double val = method == "sum"
                    ? vals.Sum(v => v.Val)
                    : vals.Average(v => v.Val);

                rows.Add(new object?[]
                {
                    aggregateId,
                    period.Start,
                    period.End,
                    val,
                    unit,
                    method,
                    contributingMembers.Count,
                    covered,
                });
            }
        }

        return rows;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> LoadMemberships(SqliteConnection connection)
    {
        var map = new Dictionary<int, List<int>>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT aggregate_entity_id, member_entity_id FROM dbe_entity_membership";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var agg = r.GetInt32(0);
            var mem = r.GetInt32(1);
            if (!map.TryGetValue(agg, out var list))
            {
                list = new List<int>();
                map[agg] = list;
            }
            list.Add(mem);
        }
        return map.ToDictionary(k => k.Key, v => (IReadOnlyList<int>)v.Value);
    }
}

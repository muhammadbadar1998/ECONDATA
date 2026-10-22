namespace EconData.Core.Diagnostics;

/// <summary>Aggregated readiness report for a machine/database.</summary>
public sealed record DoctorReport(IReadOnlyList<CheckResult> Checks)
{
    public bool HasBlockers => Checks.Any(check => check.Diagnosis == Diagnosis.Blocker);
}

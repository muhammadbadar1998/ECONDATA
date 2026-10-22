namespace EconData.Core.Diagnostics;

/// <summary>One readiness check result.</summary>
public sealed record CheckResult(string Name, Diagnosis Diagnosis, string Detail);

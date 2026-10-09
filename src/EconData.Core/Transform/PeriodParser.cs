using System.Globalization;
using System.Text.RegularExpressions;

namespace EconData.Core.Transform;

/// <summary>The parsed period range and detected frequency, or invalid when unparseable.</summary>
public readonly record struct PeriodResult(DateOnly? Start, DateOnly? End, string? Frequency)
{
    public bool IsValid => Start.HasValue && End.HasValue;

    public static PeriodResult Invalid => new(null, null, null);
}

/// <summary>
/// Parses DBnomics period literals into typed date ranges. Supported:
/// YYYY (Annual); YYYY-Qn / YYYYQn / Qn-YYYY (Quarterly); YYYY-MM / YYYY/MM
/// (Monthly); YYYY-MM-DD (exact range, Monthly frequency).
/// </summary>
public static partial class PeriodParser
{
    [GeneratedRegex(@"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$")]
    private static partial Regex DailyRegex();

    [GeneratedRegex(@"^(\d{4})[-/](\d{1,2})$")]
    private static partial Regex MonthRegex();

    [GeneratedRegex(@"^(\d{4})[-/ ]?Q(?:tr)?(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex YearQuarterRegex();

    [GeneratedRegex(@"^Q(?:tr)?(\d{1,2})[-/ ]?(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex QuarterYearRegex();

    [GeneratedRegex(@"^(\d{4})$")]
    private static partial Regex YearRegex();

    public static PeriodResult Parse(string? period)
    {
        if (string.IsNullOrWhiteSpace(period))
        {
            return PeriodResult.Invalid;
        }

        var text = period.Trim();

        var match = DailyRegex().Match(text);
        if (match.Success && TryDate(Int(match, 1), Int(match, 2), Int(match, 3), out var daily))
        {
            return new PeriodResult(daily, daily, "Monthly");
        }

        match = MonthRegex().Match(text);
        if (match.Success)
        {
            var year = Int(match, 1);
            var month = Int(match, 2);
            return month is >= 1 and <= 12
                ? new PeriodResult(new DateOnly(year, month, 1), MonthEnd(year, month), "Monthly")
                : PeriodResult.Invalid;
        }

        match = YearQuarterRegex().Match(text);
        if (match.Success)
        {
            var year = Int(match, 1);
            var quarter = Int(match, 2);
            return quarter is >= 1 and <= 4 ? QuarterResult(year, quarter) : PeriodResult.Invalid;
        }

        match = QuarterYearRegex().Match(text);
        if (match.Success)
        {
            var quarter = Int(match, 1);
            var year = Int(match, 2);
            return quarter is >= 1 and <= 4 ? QuarterResult(year, quarter) : PeriodResult.Invalid;
        }

        match = YearRegex().Match(text);
        if (match.Success)
        {
            var year = Int(match, 1);
            return year is >= 1 and <= 9999
                ? new PeriodResult(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), "Annual")
                : PeriodResult.Invalid;
        }

        return PeriodResult.Invalid;
    }

    private static PeriodResult QuarterResult(int year, int quarter)
        => new(new DateOnly(year, ((quarter - 1) * 3) + 1, 1), MonthEnd(year, quarter * 3), "Quarterly");

    private static int Int(Match match, int group)
        => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            date = new DateOnly(year, month, day);
            return true;
        }

        date = default;
        return false;
    }

    private static DateOnly MonthEnd(int year, int month)
        => new(year, month, DateTime.DaysInMonth(year, month));
}

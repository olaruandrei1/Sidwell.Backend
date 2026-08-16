using System.Globalization;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

internal static class ReportValueFormatter
{
    private const string EmDash = "—";

    public static string Auto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return EmDash;
        string trimmed = raw.Trim();

        if (trimmed.EndsWith('%') && decimal.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal pct))
            return $"{pct.ToString("0.##", CultureInfo.InvariantCulture)}%";

        if (!decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
            return trimmed;

        return FormatDecimal(v);
    }

    public static string Money(string? raw, string currency = "$")
    {
        if (string.IsNullOrWhiteSpace(raw)) return EmDash;
        if (!decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
            return raw.Trim();

        return $"{currency}{FormatDecimal(v)}";
    }

    public static string LargeNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return EmDash;
        if (!decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
            return raw.Trim();
        return FormatDecimal(v);
    }

    public static string LargeNumber(long? raw)
    {
        return raw is null ? EmDash : FormatDecimal(raw.Value);
    }

    private static string FormatDecimal(decimal v)
    {
        decimal abs = Math.Abs(v);
        if (abs >= 1_000_000_000_000m) return (v / 1_000_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "T";
        if (abs >= 1_000_000_000m) return (v / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (abs >= 1_000_000m) return (v / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (abs >= 10_000m) return v.ToString("N0", CultureInfo.InvariantCulture);
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

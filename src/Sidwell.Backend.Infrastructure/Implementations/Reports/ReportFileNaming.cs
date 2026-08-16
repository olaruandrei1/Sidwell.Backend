namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

internal static class ReportFileNaming
{
    public static string Slugify(string value)
    {
        char[] chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        string slug = new(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");

        return slug.Trim('-') is { Length: > 0 } s ? s : "note";
    }
}

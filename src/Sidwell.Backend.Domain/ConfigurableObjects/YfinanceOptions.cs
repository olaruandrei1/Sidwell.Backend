namespace Sidwell.Backend.Domain.ConfigurableObjects;

public sealed class YfinanceOptions
{
    public const string SectionName = "Yfinance";
    public string BaseUrl { get; set; } = string.Empty;
}

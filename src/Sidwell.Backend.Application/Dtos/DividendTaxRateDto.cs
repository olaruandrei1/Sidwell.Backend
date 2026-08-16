namespace Sidwell.Backend.Application.Dtos;

public record DividendTaxRateDto(
    string CountryCode,
    string RatePercent,
    string? Notes,
    string? SourceUrl,
    string? FetchedAt
);

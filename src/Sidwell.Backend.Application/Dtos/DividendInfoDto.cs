namespace Sidwell.Backend.Application.Dtos;

public record DividendInfoDto(
    string? DividendYield,
    string? ForwardDividend,
    string? ExDividendDate,
    string? PayFrequency,
    string? HistoricalGrowthCagr,
    string Status
);

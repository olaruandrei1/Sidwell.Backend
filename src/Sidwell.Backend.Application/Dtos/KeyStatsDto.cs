namespace Sidwell.Backend.Application.Dtos;

public record KeyStatsDto(
    string? FiftyTwoWeekLow,
    string? FiftyTwoWeekHigh,
    string? Beta,
    string? PeTrailing,
    string? MarketCap,
    string? EarningsDate,
    string? TargetOneYear,
    string? PriceToBook,
    string? RoeTtm,
    string? DebtToEquity,
    string? RevenueGrowthTtmYoy,
    string? EvToEbitda,
    int? AnalystBuy,
    int? AnalystHold,
    int? AnalystSell,
    string? AnalystConsensus
);

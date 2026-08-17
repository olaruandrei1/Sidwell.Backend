namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IYfinanceMetricsClient
{
    Task<YfinanceStockMetrics?> GetMetricsAsync(string symbol, CancellationToken ct = default);
}

public sealed record YfinanceStockMetrics(
    decimal? Beta,
    decimal? TargetOneYear,
    string? NextEarningsDate,
    decimal? PeTrailingTtm,
    decimal? PriceToBook,
    decimal? RoeTtm,
    decimal? DebtToEquity,
    decimal? RevenueGrowthTtmYoy,
    decimal? EvToEbitda,
    decimal? MarketCap,
    AnalystConsensus? Consensus
);

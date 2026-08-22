namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IYfinanceMetricsClient
{
    Task<YfinanceStockMetrics?> GetMetricsAsync(string symbol, CancellationToken ct = default);

    /// Near-real-time last-traded price (yfinance fast_info) — unlike price_history, this reflects
    /// the current intraday quote while the market is open, not just the last finalized daily close.
    Task<decimal?> GetLivePriceAsync(string symbol, CancellationToken ct = default);
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

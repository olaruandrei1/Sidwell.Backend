namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IFinnhubMetricsClient
{
    Task<FinnhubStockMetrics?> GetMetricsAsync(string symbol, CancellationToken ct = default);
}

public sealed record FinnhubStockMetrics(
    decimal? Beta,
    decimal? TargetOneYear,
    string? NextEarningsDate,
    decimal? PeTrailingTtm,
    decimal? PriceToBook,
    decimal? RoeTtm,
    decimal? DebtToEquity,
    decimal? RevenueGrowthTtmYoy,
    decimal? EvToEbitda,
    AnalystConsensus? Consensus
);

public sealed record AnalystConsensus(int Buy, int Hold, int Sell, string? Period);

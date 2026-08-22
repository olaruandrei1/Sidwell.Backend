namespace Sidwell.Backend.Application.Dtos;

public sealed record GatedAlgo(string AlgoName, string MissingData);

public record TickerDetailTicker(
    string Symbol,
    string Name,
    string Exchange,
    string Currency,
    string? SecCik
);

public record TickerDetailPrice(
    PriceBar? Latest,
    IReadOnlyList<PriceBar> History,
    /// Near-real-time quote while the market is open; null when unavailable (market closed with
    /// nothing newer than the last daily close, or the live-price source failed) — the frontend
    /// should fall back to Latest.Close in that case.
    string? Live
);

public record TickerDetail(
    TickerDetailTicker Ticker,
    TickerDetailPrice Price,
    CompositeScore? Composite,
    IReadOnlyList<AlgoScore> Algorithms,
    IReadOnlyList<FundamentalPeriod> Fundamentals,
    IReadOnlyList<NewsItem> News,
    HoldingDto? Holding,
    string? Note,
    bool Watchlisted,
    DividendInfoDto? Dividends,
    KeyStatsDto? KeyStats,
    IReadOnlyList<GatedAlgo> GatedAlgos
);

namespace Sidwell.Backend.Application.Dtos;

public record ScreenerResultRow(
    TickerSummary Ticker,
    CompositeScore? Composite,
    IReadOnlyDictionary<string, string?>? Metrics
);

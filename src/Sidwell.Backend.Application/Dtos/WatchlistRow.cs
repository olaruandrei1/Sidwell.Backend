namespace Sidwell.Backend.Application.Dtos;

public record WatchlistRow(
    TickerSummary Ticker,
    string? Price,
    string? DayChangePct,
    CompositeScore? Composite,
    string Status
);

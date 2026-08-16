namespace Sidwell.Backend.Application.Dtos;

public record HoldingDto(
    TickerSummary Ticker,
    string Shares,
    string AvgCost,
    string Currency,
    string MarketValue,
    string UnrealizedPnl,
    string RealizedPnl,
    string? TargetShares,
    bool TargetReached,
    string Broker
);

namespace Sidwell.Backend.Application.Dtos;

public sealed record HoldingAsOfDto(
    string Symbol,
    string Name,
    string Exchange,
    string Currency,
    string Shares,
    string AvgCost,
    string MarketValue,
    string Broker
);

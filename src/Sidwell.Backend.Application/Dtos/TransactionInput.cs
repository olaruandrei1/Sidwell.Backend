namespace Sidwell.Backend.Application.Dtos;

public record TransactionInput(
    string Symbol,
    string Side,
    string Shares,
    string? Price,
    bool PriceAuto,
    string? Fee,
    string ExecutedAt,
    string? FxRateAtExecution,
    string? TargetShares,
    string Broker,
    bool Force = false
);

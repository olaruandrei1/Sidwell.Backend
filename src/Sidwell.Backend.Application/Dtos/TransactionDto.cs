namespace Sidwell.Backend.Application.Dtos;

public record TransactionDto(
    string Symbol,
    string Side,
    string Shares,
    string Price,
    bool PriceAuto,
    string? Fee,
    string ExecutedAt,
    string? FxRateAtExecution,
    string Id,
    string CreatedAt,
    string Broker
) : TransactionInput(Symbol, Side, Shares, Price, PriceAuto, Fee, ExecutedAt, FxRateAtExecution, null, Broker);

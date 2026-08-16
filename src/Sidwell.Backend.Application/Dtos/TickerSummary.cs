namespace Sidwell.Backend.Application.Dtos;

public record TickerSummary(
    string Symbol,
    string Name,
    string Exchange,
    string Currency,
    string? Country = null,
    string? AssetType = null
);

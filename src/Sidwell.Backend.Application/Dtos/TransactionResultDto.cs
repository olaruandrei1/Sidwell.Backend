namespace Sidwell.Backend.Application.Dtos;

public record TransactionResultDto(
    HoldingDto? Holding,
    string ResolvedPrice,
    string PriceSource,
    string? PriceDate
);

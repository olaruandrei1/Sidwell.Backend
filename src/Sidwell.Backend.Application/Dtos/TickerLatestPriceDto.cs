namespace Sidwell.Backend.Application.Dtos;

public record TickerLatestPriceDto(
    string Symbol,
    string? Price,
    string Source,
    string? AsOfDate
);

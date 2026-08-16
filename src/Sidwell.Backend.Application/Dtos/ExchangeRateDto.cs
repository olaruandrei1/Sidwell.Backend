namespace Sidwell.Backend.Application.Dtos;

public sealed record ExchangeRateDto(
    string Currency,
    string RateDate,
    string RateToRon,
    string Source
);

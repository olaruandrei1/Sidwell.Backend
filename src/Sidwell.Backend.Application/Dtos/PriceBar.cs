namespace Sidwell.Backend.Application.Dtos;

public record PriceBar(
    string Date,
    string Open,
    string High,
    string Low,
    string Close,
    long Volume
);

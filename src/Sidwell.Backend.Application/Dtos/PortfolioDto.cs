namespace Sidwell.Backend.Application.Dtos;

public record PortfolioDto(
    string ReferenceCurrency,
    string TotalValue,
    string DayPnl,
    string UnrealizedPnl,
    string RealizedPnl,
    IReadOnlyList<PortfolioCurrencyTotal> ByCurrency,
    IReadOnlyList<HoldingDto> Holdings
);

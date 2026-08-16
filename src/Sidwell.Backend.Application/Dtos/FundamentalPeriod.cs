namespace Sidwell.Backend.Application.Dtos;

public record FundamentalPeriod(
    string AsOfDate,
    string Period,
    string? Revenue,
    string? NetIncome,
    string? GrossProfit,
    string? Ebit,
    string? TotalAssets,
    string? TotalLiabilities,
    string? TotalEquity,
    string? Eps,
    long? SharesOutstanding
);

namespace Sidwell.Backend.Application.Dtos;

public sealed record CurrencyAmountDto(string Currency, string Amount);

public sealed record MonthlyFinancesResponse(
    MonthlyFinanceSummaryDto Summary,
    IReadOnlyList<ExpenseItemDto> Expenses,
    IReadOnlyList<WealthAllocationDto> WealthAllocations,
    FinanceSettingsDto Settings,
    IReadOnlyList<WealthAllocationDto> CumulativeWealth,
    IReadOnlyList<HoldingAsOfDto> HoldingsAsOfMonth,
    IReadOnlyList<ExtraIncomeDto> ExtraIncomes,
    IReadOnlyList<PortfolioPnlEntryDto> TodayPortfolioPnl,
    /// True cumulative wealth total per currency (deposits minus withdrawals), unlike
    /// CumulativeWealth's per-account bucket list, which hides negative-only "phantom" buckets
    /// (standalone withdrawals with no matching deposit under the same name) so they don't render
    /// as their own account card — but they must still count against the total.
    IReadOnlyList<CurrencyAmountDto> WealthTotalByCurrency
);

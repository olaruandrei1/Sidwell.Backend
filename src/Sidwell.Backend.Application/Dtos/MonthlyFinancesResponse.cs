namespace Sidwell.Backend.Application.Dtos;

public sealed record MonthlyFinancesResponse(
    MonthlyFinanceSummaryDto Summary,
    IReadOnlyList<ExpenseItemDto> Expenses,
    IReadOnlyList<WealthAllocationDto> WealthAllocations,
    FinanceSettingsDto Settings,
    IReadOnlyList<WealthAllocationDto> CumulativeWealth,
    IReadOnlyList<HoldingAsOfDto> HoldingsAsOfMonth,
    IReadOnlyList<ExtraIncomeDto> ExtraIncomes,
    IReadOnlyList<PortfolioPnlEntryDto> TodayPortfolioPnl
);

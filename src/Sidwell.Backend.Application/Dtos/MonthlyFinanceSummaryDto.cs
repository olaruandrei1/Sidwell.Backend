namespace Sidwell.Backend.Application.Dtos;

public sealed record MonthlyFinanceSummaryDto(
    string Month,
    string NetIncome,
    string Currency,
    string? NetIncomeInRon,
    string? ExchangeRate,
    string TotalLoansAndSubs,
    string TotalUtilities,
    string TotalVariableExpenses,
    string TotalExpenses,
    string TotalAllocatedWealth,
    string FreeCash,
    string SavingsRatePct,
    string TotalExtraIncomes,
    string? TotalExtraIncomesInRon,
    IReadOnlyList<BrokerNetInvestedDto> BrokerNetInvested
);

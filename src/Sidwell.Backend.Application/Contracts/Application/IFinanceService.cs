using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public sealed record PaymentSourceEntry(
    string? Institution,
    string? InstitutionType,
    string? Currency,
    string? Type,
    string? PositionSymbol,
    string Amount
);

public sealed record AddExpenseCommand(
    string? Name,
    string? Category,
    string? Amount,
    string? Currency,
    string? Type,
    string? Status,
    string? DueDate,
    string? InterestRatePct,
    string? Month,
    bool? IsRecurring,
    IReadOnlyList<ExpenseLineItemDto>? LineItems = null,
    IReadOnlyList<PaymentSourceEntry>? PaymentSources = null,
    string? RecurringEditScope = null
);

public sealed record AddWealthAllocationCommand(
    string? Name,
    string? Institution,
    string? InstitutionType,
    string? Type,
    string? Amount,
    string? Currency,
    string? InterestRatePct,
    string? Notes,
    string? Month = null
);

public interface IFinanceService
{
    Task<FinanceSettingsDto> GetSettingsAsync(Guid userId, CancellationToken ct = default);

    Task<FinanceSettingsDto> UpdateSettingsAsync(Guid userId, FinanceSettingsDto settings, CancellationToken ct = default);

    Task<MonthlyFinancesResponse> GetMonthlyAsync(Guid userId, string month, CancellationToken ct = default);

    Task<ExpenseItemDto> AddExpenseAsync(Guid userId, AddExpenseCommand command, CancellationToken ct = default);

    Task<ExpenseItemDto> UpdateExpenseAsync(Guid userId, Guid expenseId, AddExpenseCommand command, CancellationToken ct = default);

    Task<ExpenseItemDto> UpdateExpenseStatusAsync(Guid userId, Guid expenseId, string status, string? month, CancellationToken ct = default);

    Task DeleteExpenseAsync(Guid userId, Guid expenseId, CancellationToken ct = default);

    Task<WealthAllocationDto> AddWealthAllocationAsync(Guid userId, AddWealthAllocationCommand command, CancellationToken ct = default);

    Task<WealthAllocationDto> UpdateWealthAllocationAsync(Guid userId, Guid allocationId, AddWealthAllocationCommand command, CancellationToken ct = default);

    Task DeleteWealthAllocationAsync(Guid userId, Guid allocationId, CancellationToken ct = default);

    Task<WealthSnapshotPreviewDto> GetWealthSnapshotPreviewAsync(Guid userId, string month, CancellationToken ct = default);

    Task<int> SnapshotWealthFromPriorMonthAsync(Guid userId, string month, CancellationToken ct = default);

    Task<ExpenseItemDto?> ScanReceiptAsync(Guid userId, Stream imageStream, string? contentType, CancellationToken ct = default);

    Task<ExpenseItemDto?> GetExpenseByIdAsync(Guid userId, Guid expenseId, CancellationToken ct = default);

    Task<ExpenseSeriesRangeDto?> GetExpenseSeriesRangeAsync(Guid userId, Guid expenseId, CancellationToken ct = default);

    Task<ExtraIncomeDto> AddExtraIncomeAsync(Guid userId, AddExtraIncomeCommand command, CancellationToken ct = default);

    Task DeleteExtraIncomeAsync(Guid userId, Guid extraIncomeId, CancellationToken ct = default);
}

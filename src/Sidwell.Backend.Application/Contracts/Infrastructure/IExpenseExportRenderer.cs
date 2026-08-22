namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record ExpenseExportRow(
    string Month,
    string Name,
    string Category,
    string Type,
    decimal Amount,
    string Currency,
    string Status,
    DateOnly? DueDate,
    bool IsRecurring
);

public interface IExpenseExportRenderer
{
    bool CanRender(ReportFormat format);

    Task<JournalReportFile> RenderAsync(
        IReadOnlyList<ExpenseExportRow> rows, string startMonth, string endMonth, CancellationToken ct = default);
}

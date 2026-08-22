using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class ExpenseExportService(IUnitOfWork uow, IEnumerable<IExpenseExportRenderer> renderers) : IExpenseExportService
{
    private const string SelectExpensesInRangeSql = """
        SELECT month AS Month, name AS Name, category AS Category, type AS Type,
               amount AS Amount, currency AS Currency, status AS Status,
               due_date AS DueDate, is_recurring AS IsRecurring
        FROM expenses
        WHERE user_id = @userId AND month >= @startMonth AND month <= @endMonth
        ORDER BY month ASC, created_at ASC;
        """;

    public async Task<JournalReportFile> ExportAsync(Guid userId, ExpenseExportRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse(request.Format, ignoreCase: true, out ReportFormat format))
            throw new ValidationException($"Unknown export format '{request.Format}'.");

        (string startMonth, string endMonth) = ResolveMonthRange(request);

        IReadOnlyList<ExpenseExportRow> rows = (await uow.Dapper.QueryAsync<ExpenseExportRow>(
            SelectExpensesInRangeSql, new { userId, startMonth, endMonth }, ct: ct)).ToList();

        IExpenseExportRenderer renderer = renderers.FirstOrDefault(r => r.CanRender(format))
            ?? throw new ValidationException($"No renderer available for format '{format}'.");

        return await renderer.RenderAsync(rows, startMonth, endMonth, ct);
    }

    private static (string StartMonth, string EndMonth) ResolveMonthRange(ExpenseExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Month))
            return (request.Month, request.Month);

        if (string.IsNullOrWhiteSpace(request.StartDate) || string.IsNullOrWhiteSpace(request.EndDate))
            throw new ValidationException("Either 'month' or both 'startDate' and 'endDate' are required.");

        if (!DateOnly.TryParse(request.StartDate, out DateOnly start) || !DateOnly.TryParse(request.EndDate, out DateOnly end))
            throw new ValidationException("Invalid date format for startDate/endDate.");

        if (start > end)
            throw new ValidationException("startDate must not be after endDate.");

        return (start.ToString("yyyy-MM"), end.ToString("yyyy-MM"));
    }
}

using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IExpenseExportService
{
    Task<JournalReportFile> ExportAsync(Guid userId, ExpenseExportRequest request, CancellationToken ct = default);
}

using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IJournalReportService
{
    Task<JournalReportFile> GenerateAsync(
        Guid userId,
        string symbol,
        Guid? noteId,
        ReportFormat format,
        bool includeAttachments,
        CancellationToken ct = default);
}

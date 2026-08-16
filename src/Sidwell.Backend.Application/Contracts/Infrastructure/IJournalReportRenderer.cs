using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public enum ReportFormat
{
    Pdf,
    Xlsx
}

public sealed record JournalReportFile(
    byte[] Content,
    string FileName,
    string ContentType
);

public sealed record JournalReportContext(
    string Symbol,
    string AuthorName,
    TickerNoteDto Note,
    bool IncludeAttachments
);

public interface IJournalReportRenderer
{
    bool CanRender(ReportFormat format);

    Task<JournalReportFile> RenderAsync(JournalReportContext context, ReportFormat format, CancellationToken ct = default);
}

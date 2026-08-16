using System.Text.Json;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class JournalReportService(IUnitOfWork uow, ICurrentUserAccessor currentUser, IEnumerable<IJournalReportRenderer> renderers) : IJournalReportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SelectNoteByIdSql = """
        SELECT n.id AS Id, n.title AS Title,
               n.sections::text AS SectionsJson,
               n.attachments::text AS AttachmentsJson,
               n.created_at AS CreatedAt, n.updated_at AS UpdatedAt
        FROM ticker_journal_notes n
        WHERE n.id = @noteId AND n.user_id = @userId;
        """;

    public async Task<JournalReportFile> GenerateAsync(
        Guid userId,
        string symbol,
        Guid noteId,
        ReportFormat format,
        bool includeAttachments,
        CancellationToken ct = default)
    {
        NoteRow row = await uow.Dapper.QueryFirstOrDefaultAsync<NoteRow>(SelectNoteByIdSql, new { noteId, userId }, ct: ct)
            ?? throw new NotFoundException($"Note '{noteId}' not found.");

        TickerNoteDto note = ToDto(row);

        JournalReportContext context = new(symbol.ToUpperInvariant(), ResolveAuthorName(), note, includeAttachments);

        IJournalReportRenderer renderer = renderers.FirstOrDefault(r => r.CanRender(format))
            ?? throw new NotSupportedException($"No renderer registered for report format '{format}'.");

        return await renderer.RenderAsync(context, format, ct);
    }

    private string ResolveAuthorName()
    {
        string? email = currentUser.Email;
        if (string.IsNullOrWhiteSpace(email)) return "Sidwell User";

        string local = email.Split('@')[0];
        return string.Join(' ', local.Split(['.', '_', '-', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static TickerNoteDto ToDto(NoteRow row)
    {
        IReadOnlyList<TickerNoteSectionDto> sections = ParseJson<TickerNoteSectionDto[]>(row.SectionsJson) ?? [];
        IReadOnlyList<TickerNoteAttachmentDto> attachments = ParseJson<TickerNoteAttachmentDto[]>(row.AttachmentsJson) ?? [];

        return new TickerNoteDto(row.Id.ToString(), row.Title, sections, attachments, row.CreatedAt, row.UpdatedAt);
    }

    private static T? ParseJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch { return default; }
    }

    private sealed record NoteRow(
        Guid Id,
        string Title,
        string? SectionsJson,
        string? AttachmentsJson,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt
    );
}

using System.Text.Json;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class TickerNotesService(IUnitOfWork uow) : ITickerNotesService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string FindTickerSql =
        "SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    private const string SelectNotesSql = """
        SELECT id AS Id, title AS Title,
               sections::text AS SectionsJson,
               attachments::text AS AttachmentsJson,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM ticker_journal_notes
        WHERE ticker_id = @tickerId AND user_id = @userId
        ORDER BY created_at DESC;
        """;

    private const string InsertNoteSql = """
        INSERT INTO ticker_journal_notes (ticker_id, user_id, title, sections, attachments)
        VALUES (@tickerId, @userId, @title, @sectionsJson::jsonb, @attachmentsJson::jsonb)
        RETURNING id AS Id, title AS Title,
                  sections::text AS SectionsJson,
                  attachments::text AS AttachmentsJson,
                  created_at AS CreatedAt, updated_at AS UpdatedAt;
        """;

    private const string UpdateNoteSql = """
        UPDATE ticker_journal_notes
        SET title = @title,
            sections = @sectionsJson::jsonb,
            attachments = @attachmentsJson::jsonb,
            updated_at = now()
        WHERE id = @noteId AND user_id = @userId
        RETURNING id AS Id, title AS Title,
                  sections::text AS SectionsJson,
                  attachments::text AS AttachmentsJson,
                  created_at AS CreatedAt, updated_at AS UpdatedAt;
        """;

    private const string DeleteNoteSql =
        "DELETE FROM ticker_journal_notes WHERE id = @noteId AND user_id = @userId;";

    public async Task<IReadOnlyList<TickerNoteDto>> GetBySymbolAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        Guid tickerId = await ResolveTickerIdAsync(symbol, ct);
        IReadOnlyList<NoteRow> rows = await uow.Dapper.QueryAsync<NoteRow>(SelectNotesSql, new { tickerId, userId }, ct: ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TickerNoteDto> CreateAsync(Guid userId, string symbol, UpsertTickerNoteRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ValidationException("Note title is required.");

        Guid tickerId = await ResolveTickerIdAsync(symbol, ct);

        string sectionsJson = JsonSerializer.Serialize(request.Sections ?? [], Json);
        string attachmentsJson = JsonSerializer.Serialize(request.Attachments ?? [], Json);

        NoteRow row = await uow.Dapper.QueryFirstOrDefaultAsync<NoteRow>(
            InsertNoteSql,
            new { tickerId, userId, title = request.Title.Trim(), sectionsJson, attachmentsJson },
            ct: ct) ?? throw new InvalidOperationException("Note insert returned no row.");

        return ToDto(row);
    }

    public async Task<TickerNoteDto> UpdateAsync(Guid userId, Guid noteId, UpsertTickerNoteRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ValidationException("Note title is required.");

        string sectionsJson = JsonSerializer.Serialize(request.Sections ?? [], Json);
        string attachmentsJson = JsonSerializer.Serialize(request.Attachments ?? [], Json);

        NoteRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<NoteRow>(
            UpdateNoteSql,
            new { noteId, userId, title = request.Title.Trim(), sectionsJson, attachmentsJson },
            ct: ct);

        if (row is null)
            throw new NotFoundException($"Note '{noteId}' not found.");

        return ToDto(row);
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        int affected = await uow.Dapper.ExecuteAsync(DeleteNoteSql, new { noteId, userId }, ct: ct);

        if (affected == 0)
            throw new NotFoundException($"Note '{noteId}' not found.");
    }

    private async Task<Guid> ResolveTickerIdAsync(string symbol, CancellationToken ct)
    {
        Guid? id = await uow.Dapper.ExecuteScalarAsync<Guid?>(FindTickerSql, new { symbol }, ct: ct);

        if (id is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        return id.Value;
    }

    private static TickerNoteDto ToDto(NoteRow row)
    {
        IReadOnlyList<TickerNoteSectionDto> sections = ParseJson<TickerNoteSectionDto[]>(row.SectionsJson) ?? [];
        IReadOnlyList<TickerNoteAttachmentDto> attachments = ParseJson<TickerNoteAttachmentDto[]>(row.AttachmentsJson) ?? [];

        return new TickerNoteDto(
            row.Id.ToString(),
            row.Title,
            sections,
            attachments,
            row.CreatedAt,
            row.UpdatedAt
        );
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

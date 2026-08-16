namespace Sidwell.Backend.Application.Dtos;

public sealed record TickerNoteSectionDto(string Id, string Content);

public sealed record TickerNoteAttachmentDto(string Id, string Name, string MimeType, string DataBase64);

public sealed record TickerNoteDto(
    string Id,
    string Title,
    IReadOnlyList<TickerNoteSectionDto> Sections,
    IReadOnlyList<TickerNoteAttachmentDto> Attachments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record UpsertTickerNoteRequest(
    string Title,
    IReadOnlyList<TickerNoteSectionDto>? Sections,
    IReadOnlyList<TickerNoteAttachmentDto>? Attachments
);

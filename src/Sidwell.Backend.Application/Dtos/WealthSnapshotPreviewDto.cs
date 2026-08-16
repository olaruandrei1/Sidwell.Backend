using System.Text.Json.Serialization;

namespace Sidwell.Backend.Application.Dtos;

public sealed record WealthSnapshotPreviewDto(
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PriorMonth,
    int Count,
    string Total
);

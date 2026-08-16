using System.Text.Json.Serialization;

namespace Sidwell.Backend.Application.Dtos;

public sealed record WealthAllocationDto(
    string Id,
    string Name,
    string Institution,
    string InstitutionType,
    string Type,
    string Amount,
    string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InterestRatePct,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes,
    string Month = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WealthSubItemDto>? SubItems = null
);

public sealed record WealthSubItemDto(
    string Name,
    string Amount
);

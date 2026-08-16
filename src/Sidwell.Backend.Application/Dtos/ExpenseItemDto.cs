using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Sidwell.Backend.Application.Dtos;

public sealed record ExpenseItemDto(
    string Id,
    string Name,
    string Category,
    string Amount,
    string Currency,
    string Type,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DueDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InterestRatePct,
    string CreatedAt,
    string Month,
    bool IsRecurring,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ExpenseLineItemDto>? LineItems = null
);

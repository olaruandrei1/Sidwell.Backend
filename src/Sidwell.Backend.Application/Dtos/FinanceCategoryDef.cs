namespace Sidwell.Backend.Application.Dtos;

public sealed record FinanceCategoryDef(
    string Id,
    string Name,
    string Type,
    bool IsDefault
);

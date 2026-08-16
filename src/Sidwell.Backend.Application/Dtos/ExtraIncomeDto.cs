namespace Sidwell.Backend.Application.Dtos;

public sealed record ExtraIncomeDto(
    string Id,
    string Month,
    string Name,
    string Amount,
    string Currency,
    string? Notes,
    string CreatedAt
);

public sealed record AddExtraIncomeCommand(
    string? Name,
    string? Amount,
    string? Currency,
    string? Month,
    string? Notes
);

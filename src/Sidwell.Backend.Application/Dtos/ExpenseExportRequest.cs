namespace Sidwell.Backend.Application.Dtos;

public sealed record ExpenseExportRequest(
    string Format,
    string? Month,
    string? StartDate,
    string? EndDate
);

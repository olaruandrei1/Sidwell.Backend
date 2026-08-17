namespace Sidwell.Backend.Application.Dtos;

public sealed record MonthlyIncomeDto(
    string Amount,
    string Currency
);

public sealed record FinanceCategoryTypeDef(
    string Code,
    string Label
);

public sealed record FinanceSettingsDto(
    MonthlyIncomeDto MonthlyIncome,
    IReadOnlyList<FinanceCategoryDef> Categories,
    IReadOnlyList<string> Banks,
    IReadOnlyList<string> Brokers,
    IReadOnlyList<FinanceCategoryTypeDef> CategoryTypes
);

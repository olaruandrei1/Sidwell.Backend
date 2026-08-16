namespace Sidwell.Backend.Application.Dtos;

public sealed record SimulationRow(
    string Month,
    string Income,
    string Expenses,
    string ToDeposit,
    string ToStocks,
    string DepositInterest,
    string DepositBalance,
    string StockValue,
    string NetWorth,
    IReadOnlyList<PerInstrumentMonthRow>? PerInstrument = null,
    IReadOnlyList<PerStockMonthRow>? PerStock = null
);

public sealed record PerStockMonthRow(
    string Symbol,
    string Invested,
    string Dividends,
    string Value,
    string Shares
);

public sealed record PerInstrumentMonthRow(
    string InstrumentId,
    string Name,
    string Type,
    string Currency,
    string Balance,
    string InterestEarned,
    string BalanceInBaseCurrency,
    string? Units = null,
    string? Nav = null
);

public sealed record MonthlySimulationRow(
    string Month,
    string Income,
    string Expenses,
    string ToDeposit,
    string ToStocks,
    string DepositInterest,
    string DepositBalance,
    string StockValue,
    string NetWorth,
    IReadOnlyList<PerStockMonthRow> PerStock,
    IReadOnlyList<PerInstrumentMonthRow>? PerInstrument = null
);

public sealed record SimulationResultDto(
    IReadOnlyList<SimulationRow> Rows,
    IReadOnlyDictionary<string, string> Summary,
    IReadOnlyDictionary<string, string?> Assumptions,
    IReadOnlyList<MonthlySimulationRow> MonthlyRows
);

public sealed record SavedSimulationDto(
    string Id,
    string Name,
    int HorizonYear,
    string BaseCurrency,
    SimulationConfig Config,
    string CreatedAt,
    string UpdatedAt
);

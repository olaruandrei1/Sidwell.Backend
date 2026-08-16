namespace Sidwell.Backend.Application.Dtos;

public sealed record SimulationCondition(
    string Type,
    string? Date,
    string? Amount,
    int? Count,
    string? StartDate = null
);

public sealed record DepositBondInstrumentConfig(
    string Id,
    string Name,
    string Type, // "DEPOSIT" | "BOND" | "FUND"
    string Currency, // "RON" | "EUR" | "USD" | "GBP"
    string AnnualRatePct,
    string StartingBalance,
    string? BondUnitNominal = null,
    int? MaturityYears = null,
    string? Ticker = null
);

public sealed record AllocationTargetConfig(
    string TargetType, // "INSTRUMENT" | "STOCKS"
    string? InstrumentId,
    decimal WeightPct
);

public sealed record AllocationRule(
    SimulationCondition Condition,
    string Mode,
    string? DepositPct,
    string? StocksPct,
    string? DepositAmount,
    string? StocksAmount,
    string? TargetInstrumentId = null,
    IReadOnlyList<AllocationTargetConfig>? MultiTargets = null
);

public sealed record StockRule(
    string Symbol,
    string? WeightPct,
    SimulationCondition Condition
);

public sealed record StockMemberCondition(
    string Type,
    string Value
);

public sealed record StockMemberConfig(
    string Symbol,
    StockMemberCondition Condition,
    decimal? WeightPct = null
);

public sealed record StockGroupConfig(
    decimal WeightPct,
    string Mode,
    IReadOnlyList<StockMemberConfig> Members
);

public sealed record PlannedExpense(
    string DateMonth,
    string Amount,
    string? Label
);

public sealed record StartingHolding(
    string Symbol,
    string Shares
);

public sealed record SimulationConfig(
    int HorizonYear,
    string BaseCurrency,
    string StartingDeposit,
    string DepositAnnualRatePct,
    string StockScenario,
    IReadOnlyList<AllocationRule> AllocationRules,
    IReadOnlyList<StockRule> StockRules,
    IReadOnlyList<PlannedExpense> PlannedExpenses,
    IReadOnlyList<StartingHolding>? StartingHoldings = null,
    string? CoverShortfallFrom = null,
    IReadOnlyList<StockGroupConfig>? StockGroups = null,
    bool ReinvestDividends = false,
    string? StartMonth = null,
    IReadOnlyList<DepositBondInstrumentConfig>? Instruments = null
);

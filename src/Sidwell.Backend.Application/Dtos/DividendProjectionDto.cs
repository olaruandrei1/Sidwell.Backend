namespace Sidwell.Backend.Application.Dtos;

public record DividendScenarioRow(
    int Year,
    string AnnualConservative,
    string AnnualModerate,
    string AnnualAggressive,
    string? AnnualHistoric,
    string CumulativeConservative,
    string CumulativeModerate,
    string CumulativeAggressive,
    string? CumulativeHistoric
);

public record DividendProjectionDto(
    string Ticker,
    string CurrentShares,
    string CurrentPrice,
    string DividendPerShare,
    int EndYear,
    bool Reinvest,
    IReadOnlyList<DividendScenarioRow> Scenarios,
    IReadOnlyDictionary<string, string?>? Assumptions
);

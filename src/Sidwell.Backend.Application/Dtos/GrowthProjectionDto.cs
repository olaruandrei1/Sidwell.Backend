namespace Sidwell.Backend.Application.Dtos;

public record GrowthScenarioRow(int Year, string Value, string Invested);

public record GrowthScenario(string Name, string Cagr, IReadOnlyList<GrowthScenarioRow> Rows);

public record GrowthProjectionDto(IReadOnlyList<GrowthScenario> Scenarios);

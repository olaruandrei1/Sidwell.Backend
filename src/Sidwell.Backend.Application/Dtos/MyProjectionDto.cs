namespace Sidwell.Backend.Application.Dtos;

public record MyProjectionRow(int Year, string Value, string DividendsReceived);

public record MyProjectionDto(string Shares, string AvgCost, string CurrentValue, IReadOnlyList<MyProjectionRow> Rows);

namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record IndicatorPointDto(string Date, IReadOnlyDictionary<string, double> Values);

public sealed record IndicatorSeriesDto(
    string Type,
    IReadOnlyDictionary<string, int> Params,
    IReadOnlyList<IndicatorPointDto> Points,
    string? Trend,
    string? Error
);

public interface ICoreIndicatorsClient
{
    Task<IReadOnlyList<IndicatorSeriesDto>?> GetIndicatorsAsync(Guid tickerId, IReadOnlyList<string> types, CancellationToken ct = default);
}

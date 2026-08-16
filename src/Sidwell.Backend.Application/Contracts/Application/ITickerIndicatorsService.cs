using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ITickerIndicatorsService
{
    Task<IReadOnlyList<IndicatorSeriesDto>> GetIndicatorsAsync(string symbol, IReadOnlyList<string> types, CancellationToken ct = default);
}

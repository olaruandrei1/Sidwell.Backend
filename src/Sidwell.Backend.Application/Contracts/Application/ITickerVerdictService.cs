using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ITickerVerdictService
{
    Task<TechnicalVerdictDto> GetVerdictAsync(string symbol, IReadOnlyList<string> types, CancellationToken ct = default);
}

using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IDividendProjectionService
{
    Task<DividendInfoDto> GetDividendInfoAsync(string symbol, CancellationToken ct = default);

    Task<DividendProjectionDto> ProjectAsync(string symbol, decimal shares, int endYear, bool reinvest, string userId, CancellationToken ct = default);
}

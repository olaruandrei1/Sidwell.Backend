using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IPortfolioService
{
    Task<PortfolioDto> GetAsync(Guid userId, CancellationToken ct = default);

    Task DeletePositionAsync(Guid userId, string symbol, CancellationToken ct = default);
}

using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IWatchlistService
{
    Task<IReadOnlyList<WatchlistRow>> GetAsync(Guid userId, CancellationToken ct = default);

    Task<WatchlistRow> AddAsync(Guid userId, string symbol, CancellationToken ct = default);

    Task RemoveAsync(Guid userId, string symbol, CancellationToken ct = default);
}

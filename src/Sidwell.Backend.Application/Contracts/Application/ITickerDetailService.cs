using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ITickerDetailService
{
    Task<TickerDetail?> GetBySymbolAsync(Guid userId, string symbol, CancellationToken ct = default);

    Task<IReadOnlyList<TickerSummary>> SearchAsync(string query, CancellationToken ct = default);

    Task<bool> UpdateNoteAsync(Guid userId, string symbol, string body, CancellationToken ct = default);

    Task<PaginatedResult<NewsItem>?> GetNewsPaginatedAsync(string symbol, int page = 1, int pageSize = 10, CancellationToken ct = default);

    Task<GrowthProjectionDto?> GetGrowthProjectionAsync(string symbol, decimal targetShares, CancellationToken ct = default);

    Task<MyProjectionDto?> GetMyProjectionAsync(Guid userId, string symbol, CancellationToken ct = default);

    Task<TickerLatestPriceDto> GetLatestPriceAsync(string symbol, CancellationToken ct = default);
}

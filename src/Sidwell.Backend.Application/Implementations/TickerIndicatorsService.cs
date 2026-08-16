using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;

namespace Sidwell.Backend.Application.Implementations;

public sealed class TickerIndicatorsService(IUnitOfWork uow, ICoreIndicatorsClient coreIndicators) : ITickerIndicatorsService
{
    private const string FindTickerSql =
        "SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    public async Task<IReadOnlyList<IndicatorSeriesDto>> GetIndicatorsAsync(
        string symbol, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(FindTickerSql, new { symbol }, ct: ct);
        if (tickerId is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        IReadOnlyList<IndicatorSeriesDto>? result = await coreIndicators.GetIndicatorsAsync(tickerId.Value, types, ct);
        return result ?? types.Select(t => new IndicatorSeriesDto(t, new Dictionary<string, int>(), [], null, "Indicator service unavailable")).ToList();
    }
}

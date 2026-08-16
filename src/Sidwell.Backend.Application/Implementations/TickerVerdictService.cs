using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;

namespace Sidwell.Backend.Application.Implementations;

public sealed class TickerVerdictService(IUnitOfWork uow, ICoreVerdictClient coreVerdict) : ITickerVerdictService
{
    private const string FindTickerSql =
        "SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    private const string CompositeScoreSql = """
        SELECT score
        FROM algorithm_scores
        WHERE algorithm_name = 'composite' AND ticker_id = @tickerId AND philosophy = 'BALANCED'
        ORDER BY as_of_date DESC
        LIMIT 1;
        """;

    public async Task<TechnicalVerdictDto> GetVerdictAsync(string symbol, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(FindTickerSql, new { symbol }, ct: ct);
        if (tickerId is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        decimal? compositeScore = await uow.Dapper.ExecuteScalarAsync<decimal?>(CompositeScoreSql, new { tickerId }, ct: ct);

        TechnicalVerdictDto? result = await coreVerdict.GetVerdictAsync(
            tickerId.Value, (double)(compositeScore ?? 5.0m), types, ct);

        return result ?? new TechnicalVerdictDto(0, 50, "hold", 0);
    }
}

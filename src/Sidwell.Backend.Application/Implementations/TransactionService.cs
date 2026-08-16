using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class TransactionService(IUnitOfWork uow, ISyncTrigger syncTrigger) : ITransactionService
{
    private const string FindTickerSql =
        "SELECT id AS Id, symbol AS Symbol, name AS Name, exchange AS Exchange, currency AS Currency FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    private const string CreateTickerSql = """
        INSERT INTO tickers (symbol, name, exchange, currency)
        VALUES (@symbol, @symbol, 'UNKNOWN', CASE WHEN upper(@symbol) LIKE '%.RO' THEN 'RON' ELSE 'USD' END)
        ON CONFLICT (symbol, exchange) DO UPDATE SET symbol = EXCLUDED.symbol
        RETURNING id;
        """;

    private const string FindTransactionSql =
        "SELECT id AS Id, ticker_id AS TickerId FROM transactions WHERE id = @id AND user_id = @userId;";

    private const string PriceRowAtOrBeforeSql =
        "SELECT close AS Close, date AS Date FROM price_history WHERE ticker_id = @tickerId AND date <= @date ORDER BY date DESC LIMIT 1;";

    private const string FxRateAtOrBeforeSql =
        "SELECT rate_to_ron FROM exchange_rates WHERE currency = @currency AND rate_date <= @date ORDER BY rate_date DESC LIMIT 1;";

    private const string InsertTransactionSql = """
        INSERT INTO transactions (user_id, ticker_id, side, shares, price, fee, price_auto, fx_rate_at_execution, executed_at, broker)
        VALUES (@userId, @tickerId, @side, @shares, @price, @fee, @priceAuto, @fxRate, @executedAt, @broker);
        """;

    private const string UpdateTransactionSql = """
        UPDATE transactions
        SET ticker_id = @tickerId, side = @side, shares = @shares, price = @price, fee = @fee,
            price_auto = @priceAuto, fx_rate_at_execution = @fxRate, executed_at = @executedAt, broker = @broker
        WHERE id = @id AND user_id = @userId;
        """;

    private const string DeleteTransactionSql = "DELETE FROM transactions WHERE id = @id AND user_id = @userId;";

    private const string TransactionsForTickerSql = """
        SELECT tr.id AS Id, tr.side AS Side, tr.shares::text AS Shares, tr.price::text AS Price, tr.fee::text AS Fee,
               tr.price_auto AS PriceAuto,
               tr.fx_rate_at_execution::text AS FxRateAtExecution, tr.executed_at::text AS ExecutedAt, tr.created_at::text AS CreatedAt,
               tr.broker AS Broker
        FROM transactions tr
        WHERE tr.user_id = @userId AND tr.ticker_id = @tickerId
        ORDER BY tr.executed_at DESC;
        """;

    private const string LedgerSql = """
        SELECT side AS Side, shares AS Shares, price AS Price, fee AS Fee
        FROM transactions
        WHERE user_id = @userId AND ticker_id = @tickerId
        ORDER BY executed_at ASC, created_at ASC;
        """;

    private const string UpsertHoldingSql = """
        INSERT INTO holdings (user_id, ticker_id, shares, avg_cost, realized_pnl, updated_at, broker)
        VALUES (@userId, @tickerId, @shares, @avgCost, @realizedPnl, now(), @broker)
        ON CONFLICT (user_id, ticker_id) DO UPDATE SET
            shares = EXCLUDED.shares, avg_cost = EXCLUDED.avg_cost, realized_pnl = EXCLUDED.realized_pnl,
            broker = EXCLUDED.broker, updated_at = now();
        """;

    private const string LatestBrokerForHoldingSql = """
        SELECT broker
        FROM transactions
        WHERE user_id = @userId AND ticker_id = @tickerId
        ORDER BY executed_at DESC, created_at DESC
        LIMIT 1;
        """;

    private const string UpsertTargetSql = """
        INSERT INTO portfolio_targets (user_id, ticker_id, target_shares, updated_at)
        VALUES (@userId, @tickerId, @targetShares, now())
        ON CONFLICT (user_id, ticker_id) DO UPDATE SET target_shares = EXCLUDED.target_shares, updated_at = now();
        """;

    private const string HoldingWithMarketDataSql = """
        SELECT h.shares::text AS Shares, h.avg_cost::text AS AvgCost, h.realized_pnl::text AS RealizedPnl,
               ph.close AS LatestClose, pt.target_shares::text AS TargetShares, h.broker AS Broker
        FROM holdings h
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = h.ticker_id ORDER BY date DESC LIMIT 1) ph ON true
        LEFT JOIN portfolio_targets pt ON pt.user_id = h.user_id AND pt.ticker_id = h.ticker_id
        WHERE h.user_id = @userId AND h.ticker_id = @tickerId;
        """;

    public async Task<TransactionResultDto> CreateAsync(Guid userId, TransactionInput input, CancellationToken ct = default)
    {
        string trimmed = input.Symbol.Trim().ToUpperInvariant();
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(FindTickerSql, new { symbol = trimmed }, ct: ct);
        if (ticker is null)
        {
            string defaultCurrency = trimmed.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? "RON" : "USD";
            Guid tickerId = await uow.Dapper.ExecuteScalarAsync<Guid>(
                CreateTickerSql, new { symbol = trimmed }, ct: ct);
            await syncTrigger.TriggerAsync(trimmed, ct);
            ticker = new TickerRow(tickerId, trimmed, trimmed, "UNKNOWN", defaultCurrency);
        }

        DateOnly executedDate = DateOnly.FromDateTime(DateTimeOffset.Parse(input.ExecutedAt, CultureInfo.InvariantCulture).Date);

        PriceResolution resolved = await ResolvePriceAsync(ticker.Id, input, executedDate, ct);
        await AssertPriceSanityAsync(ticker.Id, ticker.Symbol, resolved.Price, input.Force, ct);
        decimal fxRate = await ResolveFxRateAsync(ticker.Currency, executedDate, ct);
        decimal fee = ParseDecimalOrDefault(input.Fee);

        string broker = ResolveBroker(input.Broker);

        await uow.Dapper.ExecuteAsync(InsertTransactionSql, new
        {
            userId,
            tickerId = ticker.Id,
            side = input.Side,
            shares = decimal.Parse(input.Shares, CultureInfo.InvariantCulture),
            price = resolved.Price,
            fee,
            priceAuto = input.PriceAuto,
            fxRate,
            executedAt = DateTimeOffset.Parse(input.ExecutedAt, CultureInfo.InvariantCulture),
            broker
        }, ct: ct);

        if (TryParseDecimal(input.TargetShares, out decimal targetShares))
            await uow.Dapper.ExecuteAsync(UpsertTargetSql, new { userId, tickerId = ticker.Id, targetShares }, ct: ct);

        HoldingDto? holding = await RematerializeAsync(userId, ticker.Id, ct);

        return BuildResult(holding, resolved);
    }

    public async Task<TransactionResultDto> UpdateAsync(Guid userId, Guid transactionId, TransactionInput input, CancellationToken ct = default)
    {
        TransactionRow existing = await uow.Dapper.QueryFirstOrDefaultAsync<TransactionRow>(FindTransactionSql, new { id = transactionId, userId }, ct: ct)
            ?? throw new NotFoundException($"Transaction '{transactionId}' not found.");

        string trimmed = input.Symbol.Trim().ToUpperInvariant();
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(FindTickerSql, new { symbol = trimmed }, ct: ct);
        if (ticker is null)
        {
            string defaultCurrency = trimmed.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? "RON" : "USD";
            Guid tickerId = await uow.Dapper.ExecuteScalarAsync<Guid>(
                CreateTickerSql, new { symbol = trimmed }, ct: ct);
            await syncTrigger.TriggerAsync(trimmed, ct);
            ticker = new TickerRow(tickerId, trimmed, trimmed, "UNKNOWN", defaultCurrency);
        }

        DateOnly executedDate = DateOnly.FromDateTime(DateTimeOffset.Parse(input.ExecutedAt, CultureInfo.InvariantCulture).Date);

        PriceResolution resolved = await ResolvePriceAsync(ticker.Id, input, executedDate, ct);
        await AssertPriceSanityAsync(ticker.Id, ticker.Symbol, resolved.Price, input.Force, ct);
        decimal fxRate = await ResolveFxRateAsync(ticker.Currency, executedDate, ct);
        decimal fee = ParseDecimalOrDefault(input.Fee);

        string broker = ResolveBroker(input.Broker);

        await uow.Dapper.ExecuteAsync(UpdateTransactionSql, new
        {
            id = transactionId,
            userId,
            tickerId = ticker.Id,
            side = input.Side,
            shares = decimal.Parse(input.Shares, CultureInfo.InvariantCulture),
            price = resolved.Price,
            fee,
            priceAuto = input.PriceAuto,
            fxRate,
            executedAt = DateTimeOffset.Parse(input.ExecutedAt, CultureInfo.InvariantCulture),
            broker
        }, ct: ct);

        if (TryParseDecimal(input.TargetShares, out decimal targetSharesUpd))
            await uow.Dapper.ExecuteAsync(UpsertTargetSql, new { userId, tickerId = ticker.Id, targetShares = targetSharesUpd }, ct: ct);

        if (existing.TickerId != ticker.Id)
            await RematerializeAsync(userId, existing.TickerId, ct);

        HoldingDto? holding = await RematerializeAsync(userId, ticker.Id, ct);

        return BuildResult(holding, resolved);
    }

    public async Task<HoldingDto?> DeleteAsync(Guid userId, Guid transactionId, CancellationToken ct = default)
    {
        TransactionRow existing = await uow.Dapper.QueryFirstOrDefaultAsync<TransactionRow>(FindTransactionSql, new { id = transactionId, userId }, ct: ct)
            ?? throw new NotFoundException($"Transaction '{transactionId}' not found.");

        await uow.Dapper.ExecuteAsync(DeleteTransactionSql, new { id = transactionId, userId }, ct: ct);

        return await RematerializeAsync(userId, existing.TickerId, ct);
    }

    public async Task<IReadOnlyList<TransactionDto>> GetForTickerAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(FindTickerSql, new { symbol }, ct: ct);

        if (ticker is null)
            return [];

        IReadOnlyList<TransactionListRow> rows = await uow.Dapper.QueryAsync<TransactionListRow>(
            TransactionsForTickerSql, new { userId, tickerId = ticker.Id }, ct: ct);

        return rows.Select(r => new TransactionDto(
            ticker.Symbol, r.Side, r.Shares, r.Price, r.PriceAuto, r.Fee, r.ExecutedAt, r.FxRateAtExecution, r.Id.ToString(), r.CreatedAt, r.Broker ?? "TradeVille"
        )).ToList();
    }

    private async Task<PriceResolution> ResolvePriceAsync(Guid tickerId, TransactionInput input, DateOnly executedDate, CancellationToken ct)
    {
        if (!input.PriceAuto)
        {
            if (!TryParseDecimal(input.Price, out decimal manual))
                throw new ValidationException("A price is required when auto-price is off.");

            return new PriceResolution(manual, "MANUAL", null);
        }

        PriceRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<PriceRow>(
            PriceRowAtOrBeforeSql, new { tickerId, date = executedDate }, ct: ct);

        if (row is not null)
            return new PriceResolution(row.Close, "AUTO", row.Date);

        // On-demand: trigger a synchronous price sync then re-query.
        bool syncOk = await syncTrigger.TriggerPricesSyncAndWaitAsync(input.Symbol, ct);
        if (syncOk)
        {
            row = await uow.Dapper.QueryFirstOrDefaultAsync<PriceRow>(
                PriceRowAtOrBeforeSql, new { tickerId, date = executedDate }, ct: ct);
            if (row is not null)
                return new PriceResolution(row.Close, "AUTO_SYNCED", row.Date);
        }

        if (TryParseDecimal(input.Price, out decimal fallback))
            return new PriceResolution(fallback, "MANUAL_FALLBACK", null);

        throw new ValidationException(
            $"No price is available at or before {executedDate:yyyy-MM-dd} for '{input.Symbol}'. Sync trigger n-a returnat un rezultat utilizabil — enter a price manually or retry shortly.");
    }

    private static TransactionResultDto BuildResult(HoldingDto? holding, PriceResolution resolved) =>
        new(holding, FormatDecimal(resolved.Price), resolved.Source, resolved.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private async Task<decimal> ResolveFxRateAsync(string currency, DateOnly executedDate, CancellationToken ct)
    {
        if (string.Equals(currency, "RON", StringComparison.OrdinalIgnoreCase))
            return 1m;

        decimal? rate = await uow.Dapper.ExecuteScalarAsync<decimal?>(FxRateAtOrBeforeSql, new { currency, date = executedDate }, ct: ct);

        return rate ?? 1m;
    }

    private async Task<HoldingDto?> RematerializeAsync(Guid userId, Guid tickerId, CancellationToken ct)
    {
        IReadOnlyList<LedgerRow> ledger = await uow.Dapper.QueryAsync<LedgerRow>(LedgerSql, new { userId, tickerId }, ct: ct);

        decimal shares = 0m;
        decimal avgCost = 0m;
        decimal realizedPnl = 0m;

        foreach (LedgerRow tx in ledger)
        {
            if (string.Equals(tx.Side, "BUY", StringComparison.OrdinalIgnoreCase))
            {
                decimal newShares = shares + tx.Shares;
                decimal costBasis = avgCost * shares + tx.Price * tx.Shares + tx.Fee;

                avgCost = newShares == 0 ? 0m : costBasis / newShares;
                shares = newShares;
            }
            else
            {
                decimal sellShares = Math.Min(tx.Shares, shares);

                realizedPnl += (tx.Price - avgCost) * sellShares - tx.Fee;
                shares -= sellShares;

                if (shares <= 0)
                {
                    shares = 0m;
                    avgCost = 0m;
                }
            }
        }

        string broker = await uow.Dapper.QueryFirstOrDefaultAsync<string?>(
            LatestBrokerForHoldingSql, new { userId, tickerId }, ct: ct) ?? "TradeVille";

        await uow.Dapper.ExecuteAsync(UpsertHoldingSql, new { userId, tickerId, shares, avgCost, realizedPnl, broker }, ct: ct);

        if (shares == 0m)
            return null;

        TickerRow ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(
            "SELECT id AS Id, symbol AS Symbol, name AS Name, exchange AS Exchange, currency AS Currency FROM tickers WHERE id = @tickerId;",
            new { tickerId }, ct: ct)
            ?? throw new InvalidOperationException($"Ticker '{tickerId}' not found.");

        HoldingMarketRow market = await uow.Dapper.QueryFirstOrDefaultAsync<HoldingMarketRow>(HoldingWithMarketDataSql, new { userId, tickerId }, ct: ct)
            ?? throw new InvalidOperationException("Holding row missing immediately after upsert.");

        decimal marketShares = decimal.Parse(market.Shares, CultureInfo.InvariantCulture);
        decimal marketAvgCost = decimal.Parse(market.AvgCost, CultureInfo.InvariantCulture);
        decimal marketValue = marketShares * (market.LatestClose ?? marketAvgCost);
        decimal unrealizedPnl = market.LatestClose.HasValue ? (market.LatestClose.Value - marketAvgCost) * marketShares : 0m;

        bool targetReached = market.TargetShares is not null
            && decimal.TryParse(market.TargetShares, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal targetVal)
            && shares >= targetVal;

        return new HoldingDto(
            new TickerSummary(ticker.Symbol, ticker.Name, ticker.Exchange, ticker.Currency),
            market.Shares,
            market.AvgCost,
            ticker.Currency,
            FormatDecimal(marketValue),
            FormatDecimal(unrealizedPnl),
            market.RealizedPnl,
            market.TargetShares,
            targetReached,
            market.Broker ?? broker
        );
    }

    public async Task<HoldingDto?> RecalcAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        string trimmed = symbol.Trim().ToUpperInvariant();
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(FindTickerSql, new { symbol = trimmed }, ct: ct)
            ?? throw new NotFoundException($"Ticker '{trimmed}' not found.");

        return await RematerializeAsync(userId, ticker.Id, ct);
    }

    public async Task<int> RecalcAllAsync(Guid userId, CancellationToken ct = default)
    {
        IReadOnlyList<Guid> tickerIds = await uow.Dapper.QueryAsync<Guid>(
            "SELECT DISTINCT ticker_id FROM transactions WHERE user_id = @userId;",
            new { userId }, ct: ct);

        foreach (Guid tickerId in tickerIds)
            await RematerializeAsync(userId, tickerId, ct);

        return tickerIds.Count;
    }

    private async Task AssertPriceSanityAsync(Guid tickerId, string symbol, decimal price, bool force, CancellationToken ct)
    {
        if (force || price <= 0m)
            return;

        PriceRow? latest = await uow.Dapper.QueryFirstOrDefaultAsync<PriceRow>(
            "SELECT close AS Close, date AS Date FROM price_history WHERE ticker_id = @tickerId ORDER BY date DESC LIMIT 1;",
            new { tickerId }, ct: ct);

        if (latest is null || latest.Close <= 0m)
            return;

        decimal ratio = price / latest.Close;
        if (ratio > 10m || ratio < 0.1m)
        {
            string suggested = (price / (ratio > 10m ? 100m : 1m)).ToString("0.####", CultureInfo.InvariantCulture);
            throw new ValidationException(
                $"Price {price:0.####} for {symbol} is {ratio:0.#}x the latest close ({latest.Close:0.####}). " +
                $"If this is per-share, resubmit with force=true. Suggested per-share value: {suggested}.");
        }
    }

    private static string ResolveBroker(string? broker) =>
        string.IsNullOrWhiteSpace(broker) ? "TradeVille" : broker.Trim();

    private static decimal ParseDecimalOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? 0m : decimal.Parse(value, CultureInfo.InvariantCulture);

    private static bool TryParseDecimal(string? value, out decimal result)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
            return true;

        result = 0m;
        return false;
    }

    private static string FormatDecimal(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private sealed record TickerRow(Guid Id, string Symbol, string Name, string Exchange, string Currency);

    private sealed record TransactionRow(Guid Id, Guid TickerId);

    private sealed record TransactionListRow(Guid Id, string Side, string Shares, string Price, string? Fee, bool PriceAuto, string? FxRateAtExecution, string ExecutedAt, string CreatedAt, string? Broker);

    private sealed record LedgerRow(string Side, decimal Shares, decimal Price, decimal Fee);

    private sealed record HoldingMarketRow(string Shares, string AvgCost, string RealizedPnl, decimal? LatestClose, string? TargetShares, string? Broker);

    private sealed record PriceRow(decimal Close, DateOnly Date);

    private readonly record struct PriceResolution(decimal Price, string Source, DateOnly? Date);
}

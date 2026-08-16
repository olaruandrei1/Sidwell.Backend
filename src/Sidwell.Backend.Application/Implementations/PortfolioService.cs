using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class PortfolioService(IUnitOfWork uow, ISettingsService settingsService) : IPortfolioService
{
    private const string FindTickerIdSql =
        "SELECT id FROM tickers WHERE upper(symbol) = upper(@symbol) LIMIT 1;";

    private const string DeleteHoldingSql =
        "DELETE FROM holdings WHERE user_id = @userId AND ticker_id = @tickerId;";

    private const string HoldingsSql = """
        SELECT t.symbol AS Symbol, t.name AS Name, t.exchange AS Exchange, t.currency AS Currency,
               h.shares AS Shares, h.avg_cost AS AvgCost, h.realized_pnl AS RealizedPnl,
               ph.close AS LatestClose, ph_prev.close AS PrevClose,
               pt.target_shares::text AS TargetShares, h.broker AS Broker
        FROM holdings h
        JOIN tickers t ON t.id = h.ticker_id
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = h.ticker_id ORDER BY date DESC LIMIT 1) ph ON true
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = h.ticker_id ORDER BY date DESC OFFSET 1 LIMIT 1) ph_prev ON true
        LEFT JOIN portfolio_targets pt ON pt.user_id = h.user_id AND pt.ticker_id = h.ticker_id
        WHERE h.user_id = @userId;
        """;

    private const string RatesSql = """
        SELECT DISTINCT ON (currency) currency AS Currency, rate_to_ron AS RateToRon
        FROM exchange_rates
        WHERE currency = ANY(@currencies)
        ORDER BY currency, rate_date DESC;
        """;

    public async Task<PortfolioDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        SettingsDto settings = await settingsService.GetAsync(userId, ct);

        IReadOnlyList<Row> rows = await uow.Dapper.QueryAsync<Row>(HoldingsSql, new { userId }, ct: ct);

        string[] currencies = rows.Select(r => r.Currency)
            .Append(settings.ReferenceCurrency)
            .Where(c => !string.Equals(c, "RON", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Dictionary<string, decimal> ratesToRon = currencies.Length == 0
            ? new(StringComparer.OrdinalIgnoreCase)
            : (await uow.Dapper.QueryAsync<RateRow>(RatesSql, new { currencies }, ct: ct))
                .ToDictionary(r => r.Currency, r => r.RateToRon, StringComparer.OrdinalIgnoreCase);

        decimal RateToRon(string currency) =>
            string.Equals(currency, "RON", StringComparison.OrdinalIgnoreCase) ? 1m : ratesToRon.GetValueOrDefault(currency, 1m);

        decimal refRateToRon = RateToRon(settings.ReferenceCurrency);

        decimal totalValue = 0m, dayPnl = 0m, unrealizedPnl = 0m, realizedPnl = 0m;

        List<HoldingDto> holdings = [];

        Dictionary<string, decimal> byCurrency = new(StringComparer.OrdinalIgnoreCase);

        foreach (Row row in rows)
        {
            decimal factor = RateToRon(row.Currency) / refRateToRon;

            realizedPnl += row.RealizedPnl * factor;

            if (row.Shares <= 0)
                continue;

            decimal marketValueNative = row.Shares * (row.LatestClose ?? row.AvgCost);
            decimal unrealizedPnlNative = row.LatestClose.HasValue ? (row.LatestClose.Value - row.AvgCost) * row.Shares : 0m;
            decimal dayPnlNative = row.LatestClose.HasValue && row.PrevClose.HasValue
                ? (row.LatestClose.Value - row.PrevClose.Value) * row.Shares
                : 0m;

            totalValue += marketValueNative * factor;
            unrealizedPnl += unrealizedPnlNative * factor;
            dayPnl += dayPnlNative * factor;

            byCurrency[row.Currency] = byCurrency.GetValueOrDefault(row.Currency) + marketValueNative;

            bool targetReached = row.TargetShares is not null
                && decimal.TryParse(row.TargetShares, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal targetVal)
                && row.Shares >= targetVal;

            holdings.Add(new HoldingDto(
                new TickerSummary(row.Symbol, row.Name, row.Exchange, row.Currency),
                FormatDecimal(row.Shares),
                FormatDecimal(row.AvgCost),
                row.Currency,
                FormatDecimal(marketValueNative),
                FormatDecimal(unrealizedPnlNative),
                FormatDecimal(row.RealizedPnl),
                row.TargetShares,
                targetReached,
                row.Broker ?? "TradeVille"
            ));
        }

        IReadOnlyList<PortfolioCurrencyTotal> currencyTotals = byCurrency
            .Select(kvp => new PortfolioCurrencyTotal(kvp.Key, FormatDecimal(kvp.Value)))
            .OrderBy(c => c.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PortfolioDto(
            settings.ReferenceCurrency,
            FormatDecimal(totalValue),
            FormatDecimal(dayPnl),
            FormatDecimal(unrealizedPnl),
            FormatDecimal(realizedPnl),
            currencyTotals,
            holdings.OrderBy(h => h.Ticker.Symbol, StringComparer.OrdinalIgnoreCase).ToList()
        );
    }

    public async Task DeletePositionAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        string trimmed = symbol.Trim().ToUpperInvariant();

        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(FindTickerIdSql, new { symbol = trimmed }, ct: ct);
        if (tickerId is null)
            throw new NotFoundException($"No holding found for '{symbol}'.");

        int affected = await uow.Dapper.ExecuteAsync(DeleteHoldingSql, new { userId, tickerId }, ct: ct);
        if (affected == 0)
            throw new NotFoundException($"No holding found for '{symbol}'.");
    }

    private static string FormatDecimal(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private sealed record Row(
        string Symbol, string Name, string Exchange, string Currency,
        decimal Shares, decimal AvgCost, decimal RealizedPnl,
        decimal? LatestClose, decimal? PrevClose, string? TargetShares, string? Broker
    );

    private sealed record RateRow(string Currency, decimal RateToRon);
}

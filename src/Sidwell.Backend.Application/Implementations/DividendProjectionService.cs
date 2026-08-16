using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class DividendProjectionService(
    IUnitOfWork uow,
    TimeProvider clock,
    ILookupQueue queue
) : IDividendProjectionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private const decimal ConservativeGrowth = 0.06m;
    private const decimal ModerateGrowth = 0.08m;
    private const decimal AggressiveGrowth = 0.10m;
    private const string DefaultTaxCountry = "RO";

    private const string ResolveTickerIdSql = "SELECT id FROM tickers WHERE symbol = @symbol";

    private const string DividendInfoSql = """
        SELECT dividend_yield AS "DividendYield", forward_dividend AS "ForwardDividend",
               ex_dividend_date AS "ExDividendDate", pay_frequency AS "PayFrequency",
               hist_growth_cagr AS "HistGrowthCagr", fetched_at AS "FetchedAt"
        FROM ticker_dividends
        WHERE ticker_id = @tickerId
        """;

    private const string LatestPriceSql =
        "SELECT close FROM price_history WHERE ticker_id = @tickerId ORDER BY date DESC LIMIT 1";

    private const string TaxCountrySql =
        "SELECT value FROM user_settings WHERE user_id = @userId AND key = 'tax_country'";

    private const string TaxRateSql =
        "SELECT rate_percent FROM dividend_tax_rates WHERE country_code = @country";

    public async Task<DividendInfoDto> GetDividendInfoAsync(string symbol, CancellationToken ct = default)
    {
        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(ResolveTickerIdSql, new { symbol }, ct);

        if (tickerId is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        TickerDividendRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<TickerDividendRow>(DividendInfoSql, new { tickerId }, ct);

        if (row is null)
        {
            queue.TryEnqueueDividend(new DividendLookupJob(symbol, tickerId.Value, null));
            return new DividendInfoDto(null, null, null, null, null, "PENDING");
        }

        bool stale = clock.GetUtcNow() - row.FetchedAt > CacheTtl;

        if (stale)
            queue.TryEnqueueDividend(new DividendLookupJob(symbol, tickerId.Value, null));

        return new DividendInfoDto(
            row.DividendYield?.ToString(CultureInfo.InvariantCulture),
            row.ForwardDividend?.ToString(CultureInfo.InvariantCulture),
            row.ExDividendDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.PayFrequency,
            row.HistGrowthCagr?.ToString(CultureInfo.InvariantCulture),
            stale ? "STALE" : "CACHED"
        );
    }

    public async Task<DividendProjectionDto> ProjectAsync(string symbol, decimal shares, int endYear, bool reinvest, string userId, CancellationToken ct = default)
    {
        Guid? tickerId = await uow.Dapper.ExecuteScalarAsync<Guid?>(ResolveTickerIdSql, new { symbol }, ct);

        if (tickerId is null)
            throw new NotFoundException($"Ticker '{symbol}' not found.");

        TickerDividendRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<TickerDividendRow>(DividendInfoSql, new { tickerId }, ct);

        if (row is null)
        {
            Guid? requestedBy = Guid.TryParse(userId, out Guid uid) ? uid : null;

            queue.TryEnqueueDividend(new DividendLookupJob(symbol, tickerId.Value, requestedBy));

            throw new NotFoundException($"Dividend data for '{symbol}' is not available yet; a lookup has been queued.");
        }

        decimal dividendPerShare = row.ForwardDividend ?? 0m;
        decimal price = await uow.Dapper.ExecuteScalarAsync<decimal?>(LatestPriceSql, new { tickerId }, ct) ?? 0m;

        string country = await uow.Dapper.ExecuteScalarAsync<string>(TaxCountrySql, new { userId = Guid.Parse(userId) }, ct) ?? DefaultTaxCountry;

        decimal? taxRatePercent = await uow.Dapper.ExecuteScalarAsync<decimal?>(TaxRateSql, new { country }, ct);
        decimal taxRate = (taxRatePercent ?? 0m) / 100m;

        int currentYear = clock.GetUtcNow().Year;
        int years = Math.Max(0, endYear - currentYear);

        decimal? historicGrowth = row.HistGrowthCagr.HasValue ? row.HistGrowthCagr.Value / 100m : null;

        Scenario conservative = RunScenario(shares, dividendPerShare, price, ConservativeGrowth, taxRate, reinvest, currentYear, years);
        Scenario moderate = RunScenario(shares, dividendPerShare, price, ModerateGrowth, taxRate, reinvest, currentYear, years);
        Scenario aggressive = RunScenario(shares, dividendPerShare, price, AggressiveGrowth, taxRate, reinvest, currentYear, years);
        Scenario? historic = historicGrowth is { } hg
            ? RunScenario(shares, dividendPerShare, price, hg, taxRate, reinvest, currentYear, years)
            : null;

        List<DividendScenarioRow> rows = new(years);

        for (int i = 0; i < years; i++)
        {
            rows.Add(new DividendScenarioRow(
                currentYear + 1 + i,
                Money(conservative.AnnualNet[i]),
                Money(moderate.AnnualNet[i]),
                Money(aggressive.AnnualNet[i]),
                historic is null ? null : Money(historic.AnnualNet[i]),
                Money(conservative.CumulativeNet[i]),
                Money(moderate.CumulativeNet[i]),
                Money(aggressive.CumulativeNet[i]),
                historic is null ? null : Money(historic.CumulativeNet[i])
            ));
        }

        Dictionary<string, string?> assumptions = new()
        {
            ["reinvest"] = reinvest ? "true" : "false",
            ["conservativeGrowthPct"] = "6",
            ["moderateGrowthPct"] = "8",
            ["aggressiveGrowthPct"] = "10",
            ["historicGrowthPct"] = row.HistGrowthCagr?.ToString(CultureInfo.InvariantCulture),
            ["taxCountry"] = country,
            ["taxRatePct"] = (taxRatePercent ?? 0m).ToString(CultureInfo.InvariantCulture),
            ["reinvestPrice"] = "constant at current price",
            ["dividendPerShareStart"] = Money(dividendPerShare),
            ["finalShares.conservative"] = Shares(conservative.FinalShares),
            ["finalShares.moderate"] = Shares(moderate.FinalShares),
            ["finalShares.aggressive"] = Shares(aggressive.FinalShares),
            ["finalShares.historic"] = historic is null ? null : Shares(historic.FinalShares),
        };

        return new DividendProjectionDto(
            symbol,
            Shares(shares),
            Money(price),
            Money(dividendPerShare),
            endYear,
            reinvest,
            rows,
            assumptions
        );
    }

    private static Scenario RunScenario(decimal shares, decimal dividendPerShare, decimal price, decimal growth, decimal taxRate, bool reinvest, int currentYear, int years)
    {
        decimal currentShares = shares;
        decimal dps = dividendPerShare;
        decimal cumulativeNet = 0m;
        decimal[] annual = new decimal[years];
        decimal[] cumulative = new decimal[years];

        for (int i = 0; i < years; i++)
        {
            decimal gross = currentShares * dps;
            decimal net = gross * (1 - taxRate);

            cumulativeNet += net;

            if (reinvest && price > 0m)
                currentShares += net / price;

            annual[i] = net;
            cumulative[i] = cumulativeNet;

            dps *= 1 + growth;
        }

        return new Scenario(annual, cumulative, currentShares);
    }

    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Shares(decimal value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private sealed record Scenario(decimal[] AnnualNet, decimal[] CumulativeNet, decimal FinalShares);

    private sealed record TickerDividendRow(
        decimal? DividendYield,
        decimal? ForwardDividend,
        DateOnly? ExDividendDate,
        string? PayFrequency,
        decimal? HistGrowthCagr,
        DateTimeOffset FetchedAt
    );
}

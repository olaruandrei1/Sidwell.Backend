using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class FinanceSimulationService(IUnitOfWork uow, TimeProvider clock) : IFinanceSimulationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const decimal ConservativeGrowth = 5m;
    private const decimal ModerateGrowth = 8m;
    private const decimal HistoricalGrowth = 10m;
    private const string DefaultTaxCountry = "RO";

    private const string IncomeSql =
        "SELECT monthly_income_amount FROM finance_settings WHERE user_id = @userId;";

    private const string RecurringExpenseSql =
        "SELECT COALESCE(SUM(amount), 0) FROM expenses WHERE user_id = @userId AND is_recurring = true;";

    private const string PriceAndCurrencySql = """
        SELECT t.currency AS Currency,
               (SELECT close FROM price_history WHERE ticker_id = t.id ORDER BY date DESC LIMIT 1) AS Close
        FROM tickers t
        WHERE upper(t.symbol) = upper(@symbol)
        LIMIT 1;
        """;

    private const string FxRateSql =
        "SELECT rate_to_ron FROM exchange_rates WHERE currency = @currency ORDER BY rate_date DESC LIMIT 1;";

    private const string DividendDataSql = """
        SELECT t.symbol AS Symbol, td.forward_dividend AS ForwardDividend, td.hist_growth_cagr AS HistGrowthCagr
        FROM ticker_dividends td
        JOIN tickers t ON t.id = td.ticker_id
        WHERE upper(t.symbol) = ANY(@symbols);
        """;

    private const string DividendYieldHistorySql = """
        SELECT DISTINCT ON (upper(symbol))
               symbol    AS "Symbol",
               yield_pct AS "YieldPct"
        FROM   dividend_yield_history
        WHERE  upper(symbol) = ANY(@symbols)
        ORDER  BY upper(symbol), year DESC;
        """;

    private const string TaxCountrySql =
        "SELECT value FROM user_settings WHERE user_id = @userId AND key = 'tax_country';";

    private const string TaxRateSql =
        "SELECT rate_percent FROM dividend_tax_rates WHERE country_code = @country;";

    private const string ListSql = """
        SELECT id AS Id, name AS Name, horizon_year AS HorizonYear, base_currency AS BaseCurrency,
               config::text AS Config, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM finance_simulations
        WHERE user_id = @userId
        ORDER BY updated_at DESC;
        """;

    private const string NameExistsSql =
        "SELECT 1 FROM finance_simulations WHERE user_id = @userId AND name = @name AND id <> @excludeId LIMIT 1;";

    private const string InsertSql = """
        INSERT INTO finance_simulations (user_id, name, horizon_year, base_currency, config)
        VALUES (@userId, @name, @horizonYear, @baseCurrency, @config::jsonb)
        RETURNING id AS Id, created_at AS CreatedAt, updated_at AS UpdatedAt;
        """;

    private const string UpdateSql = """
        UPDATE finance_simulations
        SET name = @name, horizon_year = @horizonYear, base_currency = @baseCurrency,
            config = @config::jsonb, updated_at = now()
        WHERE id = @id AND user_id = @userId
        RETURNING id AS Id, created_at AS CreatedAt, updated_at AS UpdatedAt;
        """;

    private const string DeleteSql = "DELETE FROM finance_simulations WHERE id = @id AND user_id = @userId;";

    public async Task<IReadOnlyList<SavedSimulationDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        IReadOnlyList<SavedRow> rows = await uow.Dapper.QueryAsync<SavedRow>(ListSql, new { userId }, ct);

        return rows.Select(Map).ToList();
    }

    public async Task<SavedSimulationDto> CreateAsync(Guid userId, string name, SimulationConfig config, CancellationToken ct = default)
    {
        string cleanName = RequireName(name);

        bool exists = await uow.Dapper.ExecuteScalarAsync<int?>(
            NameExistsSql, new { userId, name = cleanName, excludeId = Guid.Empty }, ct) is not null;

        if (exists)
            throw new ConflictException($"A simulation named '{cleanName}' already exists.");

        SavedIdRow row = await uow.Dapper.QueryFirstOrDefaultAsync<SavedIdRow>(
            InsertSql,
            new
            {
                userId,
                name = cleanName,
                horizonYear = config.HorizonYear,
                baseCurrency = NormalizeCurrency(config.BaseCurrency),
                config = JsonSerializer.Serialize(config, Json),
            }, ct)
            ?? throw new InvalidOperationException("Simulation insert did not return a row.");

        return new SavedSimulationDto(
            row.Id.ToString(), cleanName, config.HorizonYear, NormalizeCurrency(config.BaseCurrency),
            config, FormatTimestamp(row.CreatedAt), FormatTimestamp(row.UpdatedAt));
    }

    public async Task<SavedSimulationDto> UpdateAsync(Guid userId, Guid id, string name, SimulationConfig config, CancellationToken ct = default)
    {
        string cleanName = RequireName(name);

        bool clash = await uow.Dapper.ExecuteScalarAsync<int?>(
            NameExistsSql, new { userId, name = cleanName, excludeId = id }, ct) is not null;

        if (clash)
            throw new ConflictException($"A simulation named '{cleanName}' already exists.");

        SavedIdRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<SavedIdRow>(
            UpdateSql,
            new
            {
                id,
                userId,
                name = cleanName,
                horizonYear = config.HorizonYear,
                baseCurrency = NormalizeCurrency(config.BaseCurrency),
                config = JsonSerializer.Serialize(config, Json),
            }, ct);

        if (row is null)
            throw new NotFoundException($"Simulation '{id}' not found.");

        return new SavedSimulationDto(
            row.Id.ToString(), cleanName, config.HorizonYear, NormalizeCurrency(config.BaseCurrency),
            config, FormatTimestamp(row.CreatedAt), FormatTimestamp(row.UpdatedAt));
    }

    public Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        uow.Dapper.ExecuteAsync(DeleteSql, new { id, userId }, ct);

    public async Task<SimulationResultDto> RunAsync(Guid userId, SimulationConfig config, CancellationToken ct = default)
    {
        if (config is null)
            throw new ValidationException("A simulation config is required.");

        DateOnly start = ParseStartMonth(config.StartMonth) ?? FirstOfMonth(clock.GetUtcNow());
        string baseCurrency = NormalizeCurrency(config.BaseCurrency);

        decimal monthlyIncome = await uow.Dapper.ExecuteScalarAsync<decimal?>(IncomeSql, new { userId }, ct) ?? 0m;
        decimal fixedExpense = await uow.Dapper.ExecuteScalarAsync<decimal?>(RecurringExpenseSql, new { userId }, ct) ?? 0m;

        decimal stockGrowthPct = ResolveScenario(config.StockScenario);

        (IReadOnlyDictionary<string, decimal> startingPrices, List<string> unpriced) =
            await SeedPricesAsync(config, baseCurrency, ct);

        Dictionary<string, decimal> startingShares = new(StringComparer.OrdinalIgnoreCase);
        foreach (StartingHolding holding in config.StartingHoldings ?? [])
        {
            if (string.IsNullOrWhiteSpace(holding.Symbol))
                continue;

            startingShares[holding.Symbol.Trim()] = ParseDecimal(holding.Shares);
        }

        IReadOnlyList<EngineStockGroup> stockGroups = (config.StockGroups ?? [])
            .Select(ToEngineGroup)
            .Where(g => g.Members.Count > 0)
            .ToList();

        (IReadOnlyDictionary<string, decimal> dividendPerShare, IReadOnlyDictionary<string, decimal> dividendGrowth) =
            await SeedDividendsAsync(CollectSymbols(config), startingPrices, ct);

        decimal dividendTaxRate = await ResolveDividendTaxRateAsync(userId, ct);

        Dictionary<string, decimal> engineFxRates = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> instrumentCurrencies = new(StringComparer.OrdinalIgnoreCase);
        foreach (DepositBondInstrumentConfig ic in config.Instruments ?? [])
        {
            string cur = NormalizeCurrency(ic.Currency);
            if (!string.Equals(cur, baseCurrency, StringComparison.OrdinalIgnoreCase))
                instrumentCurrencies.Add(cur);
        }
        Dictionary<string, decimal> fxCacheForEngine = new(StringComparer.OrdinalIgnoreCase);
        foreach (string cur in instrumentCurrencies)
        {
            decimal rateToRon = await RateToRonAsync(cur, fxCacheForEngine, ct);
            if (string.Equals(baseCurrency, "RON", StringComparison.OrdinalIgnoreCase))
            {
                engineFxRates[cur] = rateToRon;
            }
            else
            {
                decimal baseToRon = await RateToRonAsync(baseCurrency, fxCacheForEngine, ct);
                engineFxRates[cur] = baseToRon > 0m ? rateToRon / baseToRon : rateToRon;
            }
        }

        List<DepositBondInstrumentConfig> instrumentConfigs = [.. config.Instruments ?? []];
        Dictionary<string, decimal> fundNavPrices = new(StringComparer.OrdinalIgnoreCase);

        List<string> fundTickers = instrumentConfigs
            .Where(i => string.Equals(i.Type, "FUND", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(i.Ticker))
            .Select(i => i.Ticker!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fundTickers.Count > 0)
        {
            Dictionary<string, decimal> liveFundPrices = await FetchLivePricesFromYahooAsync(fundTickers, ct);
            foreach (DepositBondInstrumentConfig fc in instrumentConfigs)
            {
                if (!string.Equals(fc.Type, "FUND", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fc.Ticker))
                    continue;
                if (liveFundPrices.TryGetValue(fc.Ticker!.Trim(), out decimal navPrice) && navPrice > 0m)
                    fundNavPrices[fc.Id] = navPrice;
            }
        }

        List<EngineInstrument> engineInstruments = instrumentConfigs
            .Select(ic => ToEngineInstrument(ic, fundNavPrices.GetValueOrDefault(ic.Id)))
            .ToList();

        EngineInput input = new(
            Start: start,
            HorizonYear: config.HorizonYear,
            MonthlyIncome: monthlyIncome,
            FixedMonthlyExpense: fixedExpense,
            StartingDeposit: ParseDecimal(config.StartingDeposit),
            DepositAnnualRatePct: ParseDecimal(config.DepositAnnualRatePct),
            StockAnnualGrowthPct: stockGrowthPct,
            AllocationRules: (config.AllocationRules ?? []).Select(ToEngineAllocation).ToList(),
            StockRules: (config.StockRules ?? []).Select(ToEngineStock).ToList(),
            PlannedExpenses: (config.PlannedExpenses ?? [])
                .Select(ToEnginePlanned)
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList(),
            StartingPrices: startingPrices,
            StartingShares: startingShares,
            Shortfall: ParseShortfall(config.CoverShortfallFrom),
            StockGroups: stockGroups,
            StartingInvested: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            DividendPerShare: dividendPerShare,
            DividendGrowthRate: dividendGrowth,
            DividendTaxRate: dividendTaxRate,
            ReinvestDividends: config.ReinvestDividends,
            Instruments: engineInstruments,
            FxRates: engineFxRates,
            BaseCurrency: baseCurrency);

        EngineResult result = FinanceSimulationEngine.Run(input);

        IReadOnlyList<SimulationRow> rows = BucketByYear(result.Rows);
        IReadOnlyList<MonthlySimulationRow> monthlyRows = ToMonthlyRows(result.Rows);

        Dictionary<string, string> summary = new()
        {
            ["finalNetWorth"] = Money(result.Summary.FinalNetWorth),
            ["finalDeposit"] = Money(result.Summary.FinalDeposit),
            ["finalPortfolio"] = Money(result.Summary.FinalPortfolio),
            ["totalInvested"] = Money(result.Summary.TotalInvested),
            ["totalContributedToDeposit"] = Money(result.Summary.TotalToDeposit),
            ["totalInterest"] = Money(result.Summary.TotalInterest),
            ["totalIncome"] = Money(result.Summary.TotalIncome),
            ["totalExpenses"] = Money(result.Summary.TotalExpenses),
            ["totalStockCapitalGains"] = Money(result.Summary.TotalStockCapitalGains),
            ["totalDepositInterest"] = Money(result.Summary.TotalDepositInterest),
            ["totalBondCoupons"] = Money(result.Summary.TotalBondCoupons),
            ["currencyBreakdownJson"] = System.Text.Json.JsonSerializer.Serialize(result.Summary.CurrencyBreakdown),
            ["marketExposureJson"] = System.Text.Json.JsonSerializer.Serialize(result.Summary.MarketExposure),
            ["netWorthByCurrencyJson"] = System.Text.Json.JsonSerializer.Serialize(result.Summary.NetWorthByCurrency),
            ["stockValueByMarketJson"] = System.Text.Json.JsonSerializer.Serialize(result.Summary.StockValueByMarket),
            ["perStockSummaryJson"] = System.Text.Json.JsonSerializer.Serialize(
                result.FinalShares.Keys
                    .Select(sym => new {
                        symbol = sym,
                        shares = Math.Round(result.FinalShares.GetValueOrDefault(sym), 2),
                        invested = Math.Round(result.FinalInvestedPerSymbol.GetValueOrDefault(sym), 2),
                        dividends = Math.Round(result.TotalDividendsPerSymbol.GetValueOrDefault(sym), 2),
                        value = Math.Round(monthlyRows.Count > 0
                            ? (monthlyRows[^1].PerStock?.FirstOrDefault(p => string.Equals(p.Symbol, sym, StringComparison.OrdinalIgnoreCase))?.Value is string v
                                ? decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv) ? pv : 0m
                                : 0m)
                            : 0m, 2),
                        market = sym.ToUpperInvariant() switch {
                            var s when s.EndsWith(".RO") => "BVB (România)",
                            var s when s.EndsWith(".L")  => "UK (Marea Britanie)",
                            var s when s.EndsWith(".AS") => "NL (Amsterdam)",
                            var s when s.EndsWith(".DE") => "DE (Germania)",
                            _ => "US (Statele Unite)"
                        }
                    })
                    .Where(x => x.shares > 0 || x.invested > 0)
                    .OrderBy(x => x.market).ThenBy(x => x.symbol)
                    .ToList())
        };

        Dictionary<string, string?> assumptions = new()
        {
            ["startMonth"] = start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["horizonYear"] = Math.Max(config.HorizonYear, start.Year).ToString(CultureInfo.InvariantCulture),
            ["baseCurrency"] = baseCurrency,
            ["monthlyIncome"] = Money(monthlyIncome),
            ["fixedMonthlyExpense"] = Money(fixedExpense),
            ["startingDeposit"] = Money(ParseDecimal(config.StartingDeposit)),
            ["depositAnnualRatePct"] = FormatPct(ParseDecimal(config.DepositAnnualRatePct)),
            ["stockScenario"] = string.IsNullOrWhiteSpace(config.StockScenario) ? "MODERATE" : config.StockScenario.Trim(),
            ["stockAnnualGrowthPct"] = FormatPct(stockGrowthPct),
            ["coverShortfallFrom"] = ParseShortfall(config.CoverShortfallFrom) == ShortfallPolicy.Deposit ? "DEPOSIT" : "NONE",
            ["unpricedSymbols"] = unpriced.Count == 0 ? null : string.Join(", ", unpriced),
            ["stockAllocation"] = stockGroups.Count > 0 ? "GROUPS" : "RULES",
            ["reinvestDividends"] = config.ReinvestDividends ? "true" : "false",
            ["dividendTaxRatePct"] = FormatPct(dividendTaxRate * 100m),
            ["note"] = "Rows are yearly buckets (flows summed, balances end-of-year); monthlyRows carry the per-symbol monthly breakdown. Stock prices seeded from latest close in base currency and grown by the scenario rate; FX held constant. Dividends accrue monthly from the forward dividend, grown yearly and taxed at the flat rate. Unallocated percent surplus stays as untracked cash.",
        };

        return new SimulationResultDto(rows, summary, assumptions, monthlyRows);
    }

    private async Task<(IReadOnlyDictionary<string, decimal> PerShare, IReadOnlyDictionary<string, decimal> Growth)> SeedDividendsAsync(
        IReadOnlyCollection<string> symbols,
        IReadOnlyDictionary<string, decimal> startingPrices,
        CancellationToken ct)
    {
        Dictionary<string, decimal> perShare = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> growth = new(StringComparer.OrdinalIgnoreCase);

        if (symbols.Count == 0)
            return (perShare, growth);

        string[] upper = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToArray();

        try
        {
            IReadOnlyList<DividendYieldHistoryRow> histRows = await uow.Dapper.QueryAsync<DividendYieldHistoryRow>(
                DividendYieldHistorySql, new { symbols = upper }, ct);

            foreach (DividendYieldHistoryRow hr in histRows)
            {
                if (string.IsNullOrWhiteSpace(hr.Symbol)) continue;
                string sym = hr.Symbol.Trim().ToUpperInvariant();

                // yield_pct → absolute dividend per share in base currency: yieldPct/100 * price
                decimal price = startingPrices.GetValueOrDefault(sym);
                if (price <= 0m)
                    price = sym.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? 25m : 100m;
                perShare[sym] = hr.YieldPct / 100m * price;
            }
        }
        catch
        {
            // dividend_yield_history table may not exist yet
        }

        HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sym in upper)
            if (!perShare.ContainsKey(sym))
                missing.Add(sym);

        if (missing.Count > 0)
        {
            IReadOnlyList<DividendRow> rows = await uow.Dapper.QueryAsync<DividendRow>(DividendDataSql, new { symbols = missing.ToArray() }, ct);

            foreach (DividendRow row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Symbol)) continue;
                string symbol = row.Symbol.Trim();

                if (!perShare.ContainsKey(symbol) && row.ForwardDividend is { } forward && forward > 0m)
                    perShare[symbol] = forward;

                if (row.HistGrowthCagr is { } cagr)
                    growth[symbol] = cagr / 100m;
            }
        }

        return (perShare, growth);
    }

    private sealed record DividendYieldHistoryRow(string Symbol, decimal YieldPct);

    private async Task<decimal> ResolveDividendTaxRateAsync(Guid userId, CancellationToken ct)
    {
        string country = await uow.Dapper.ExecuteScalarAsync<string>(TaxCountrySql, new { userId }, ct) ?? DefaultTaxCountry;

        decimal? ratePercent = await uow.Dapper.ExecuteScalarAsync<decimal?>(TaxRateSql, new { country }, ct);

        return (ratePercent ?? 0m) / 100m;
    }

    private static IReadOnlyCollection<string> CollectSymbols(SimulationConfig config)
    {
        HashSet<string> symbols = new(StringComparer.OrdinalIgnoreCase);

        foreach (StockRule rule in config.StockRules ?? [])
            if (!string.IsNullOrWhiteSpace(rule.Symbol))
                symbols.Add(rule.Symbol.Trim());

        foreach (StockGroupConfig group in config.StockGroups ?? [])
            foreach (StockMemberConfig member in group.Members ?? [])
                if (!string.IsNullOrWhiteSpace(member.Symbol))
                    symbols.Add(member.Symbol.Trim());

        foreach (StartingHolding holding in config.StartingHoldings ?? [])
            if (!string.IsNullOrWhiteSpace(holding.Symbol))
                symbols.Add(holding.Symbol.Trim());

        return symbols;
    }

    private static IReadOnlyList<MonthlySimulationRow> ToMonthlyRows(IReadOnlyList<EngineRow> rows)
    {
        List<MonthlySimulationRow> monthly = new(rows.Count);

        foreach (EngineRow row in rows)
        {
            HashSet<string> symbols = new(StringComparer.OrdinalIgnoreCase);

            foreach (string symbol in row.PerStockInvested.Keys)
                symbols.Add(symbol);

            foreach (string symbol in row.PerStockDividends.Keys)
                symbols.Add(symbol);

            foreach (string symbol in row.PerStockShares.Keys)
                symbols.Add(symbol);

            List<PerStockMonthRow> perStock = symbols
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new PerStockMonthRow(
                    s,
                    Money(row.PerStockInvested.GetValueOrDefault(s)),
                    Money(row.PerStockDividends.GetValueOrDefault(s)),
                    Money(row.PerStockValue.GetValueOrDefault(s)),
                    FormatQty(row.PerStockShares.GetValueOrDefault(s))))
                .ToList();

            List<PerInstrumentMonthRow> perInstrument = (row.PerInstrument ?? [])
                .Select(i => new PerInstrumentMonthRow(
                    i.Id,
                    i.Name,
                    i.Type,
                    i.Currency,
                    Money(i.Balance),
                    Money(i.InterestEarned),
                    Money(i.BalanceInBaseCurrency),
                    i.Units > 0m ? FormatQty(i.Units) : null,
                    i.Nav > 0m ? Money(i.Nav) : null))
                .ToList();

            monthly.Add(new MonthlySimulationRow(
                row.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                Money(row.Income),
                Money(row.Expenses),
                Money(row.ToDeposit),
                Money(row.ToStocks),
                Money(row.DepositInterest),
                Money(row.DepositBalance),
                Money(row.StockValue),
                Money(row.NetWorth),
                perStock,
                perInstrument));
        }

        return monthly;
    }

    private async Task<(IReadOnlyDictionary<string, decimal> Prices, List<string> Unpriced)> SeedPricesAsync(
        SimulationConfig config, string baseCurrency, CancellationToken ct)
    {
        IReadOnlyCollection<string> symbols = CollectSymbols(config);

        Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> fxCache = new(StringComparer.OrdinalIgnoreCase);
        List<string> unpriced = [];

        Dictionary<string, decimal> liveYahooPrices = await FetchLivePricesFromYahooAsync(symbols, ct);

        foreach (string symbol in symbols)
        {
            PriceRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<PriceRow>(PriceAndCurrencySql, new { symbol }, ct);

            decimal basePrice = 0m;
            if (liveYahooPrices.TryGetValue(symbol, out decimal yahooPrice) && yahooPrice > 0m)
            {
                string yahooCurr = symbol.ToUpperInvariant() switch
                {
                    var s when s.EndsWith(".RO") => "RON",
                    var s when s.EndsWith(".L") => "GBP",
                    var s when s.EndsWith(".AS") || s.EndsWith(".DE") => "EUR",
                    _ => "USD"
                };
                basePrice = await ConvertToBaseAsync(yahooPrice, yahooCurr, baseCurrency, fxCache, ct);
            }
            else if (row?.Close is { } close && close > 0m)
            {
                basePrice = await ConvertToBaseAsync(close, (row.Currency ?? baseCurrency).Trim(), baseCurrency, fxCache, ct);
            }
            else
            {
                basePrice = symbol.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? 25.00m : 100.00m;
                unpriced.Add(symbol);
            }

            prices[symbol] = basePrice;
        }

        return (prices, unpriced);
    }

    private static readonly ConcurrentDictionary<string, (decimal Price, DateTime FetchedAt)> LivePriceCache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, decimal>> FetchLivePricesFromYahooAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        Dictionary<string, decimal> result = new(StringComparer.OrdinalIgnoreCase);
        List<string> list = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return result;

        List<string> toFetch = [];
        DateTime now = DateTime.UtcNow;

        foreach (string sym in list)
        {
            if (LivePriceCache.TryGetValue(sym, out var cached) && (now - cached.FetchedAt).TotalMinutes < 5)
            {
                result[sym] = cached.Price;
            }
            else
            {
                toFetch.Add(sym);
            }
        }

        if (toFetch.Count == 0)
            return result;

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
            string baseUrl = Environment.GetEnvironmentVariable("YahooBridge__BaseUrl") ?? "http://yahoo:8000/";
            if (!baseUrl.EndsWith('/')) baseUrl += "/";

            string symbolsQuery = string.Join(",", toFetch);
            string url = $"{baseUrl}api/v1/live-prices?symbols={Uri.EscapeDataString(symbolsQuery)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(ct);
                Dictionary<string, decimal>? dict = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, Json);
                if (dict is not null)
                {
                    foreach ((string key, decimal val) in dict)
                    {
                        if (val > 0m)
                        {
                            result[key] = val;
                            LivePriceCache[key] = (val, now);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back silently to DB/defaults if Yahoo bridge service is offline or during standalone unit tests
        }

        return result;
    }

    private async Task<decimal> ConvertToBaseAsync(
        decimal amount, string from, string baseCurrency, Dictionary<string, decimal> fxCache, CancellationToken ct)
    {
        from = NormalizeCurrency(from);

        if (string.Equals(from, baseCurrency, StringComparison.OrdinalIgnoreCase))
            return amount;

        decimal amountInRon = string.Equals(from, "RON", StringComparison.OrdinalIgnoreCase)
            ? amount
            : amount * await RateToRonAsync(from, fxCache, ct);

        if (string.Equals(baseCurrency, "RON", StringComparison.OrdinalIgnoreCase))
            return amountInRon;

        decimal baseRate = await RateToRonAsync(baseCurrency, fxCache, ct);

        return baseRate <= 0m ? amountInRon : amountInRon / baseRate;
    }

    private async Task<decimal> RateToRonAsync(string currency, Dictionary<string, decimal> fxCache, CancellationToken ct)
    {
        if (fxCache.TryGetValue(currency, out decimal cached))
            return cached;

        decimal rate = await uow.Dapper.ExecuteScalarAsync<decimal?>(FxRateSql, new { currency }, ct) ?? 1m;

        fxCache[currency] = rate;
        return rate;
    }

    private static IReadOnlyList<SimulationRow> BucketByYear(IReadOnlyList<EngineRow> rows)
    {
        List<SimulationRow> buckets = [];

        foreach (IGrouping<int, EngineRow> group in rows.GroupBy(r => r.Month.Year).OrderBy(g => g.Key))
        {
            decimal income = 0m, expenses = 0m, toDeposit = 0m, toStocks = 0m, depositInterest = 0m;
            EngineRow last = group.First();
            Dictionary<string, decimal> yearStockInvested = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, decimal> yearStockDividends = new(StringComparer.OrdinalIgnoreCase);

            foreach (EngineRow r in group)
            {
                income += r.Income;
                expenses += r.Expenses;
                toDeposit += r.ToDeposit;
                toStocks += r.ToStocks;
                depositInterest += r.DepositInterest;

                foreach ((string sym, decimal v) in r.PerStockInvested)
                    yearStockInvested[sym] = yearStockInvested.GetValueOrDefault(sym) + v;
                foreach ((string sym, decimal v) in r.PerStockDividends)
                    yearStockDividends[sym] = yearStockDividends.GetValueOrDefault(sym) + v;

                if (r.Month >= last.Month)
                    last = r;
            }

            HashSet<string> symbols = new(StringComparer.OrdinalIgnoreCase);
            foreach (string s in yearStockInvested.Keys) symbols.Add(s);
            foreach (string s in yearStockDividends.Keys) symbols.Add(s);
            foreach (string s in last.PerStockShares.Keys) symbols.Add(s);

            List<PerStockMonthRow> perStock = symbols
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new PerStockMonthRow(
                    s,
                    Money(yearStockInvested.GetValueOrDefault(s)),
                    Money(yearStockDividends.GetValueOrDefault(s)),
                    Money(last.PerStockValue.GetValueOrDefault(s)),
                    FormatQty(last.PerStockShares.GetValueOrDefault(s))))
                .Where(r => parseDecimalFast(r.Invested) > 0 || parseDecimalFast(r.Value) > 0 || parseDecimalFast(r.Shares) > 0)
                .ToList();

            List<PerInstrumentMonthRow> perInstrument = (last.PerInstrument ?? [])
                .Select(i => new PerInstrumentMonthRow(
                    i.Id, i.Name, i.Type, i.Currency,
                    Money(i.Balance), Money(i.InterestEarned), Money(i.BalanceInBaseCurrency),
                    i.Units > 0m ? FormatQty(i.Units) : null,
                    i.Nav > 0m ? Money(i.Nav) : null))
                .ToList();

            buckets.Add(new SimulationRow(
                last.Month.ToString("yyyy-12", CultureInfo.InvariantCulture),
                Money(income), Money(expenses), Money(toDeposit), Money(toStocks), Money(depositInterest),
                Money(last.DepositBalance), Money(last.StockValue), Money(last.NetWorth),
                perInstrument.Count > 0 ? perInstrument : null,
                perStock.Count > 0 ? perStock : null));
        }

        return buckets;

        static decimal parseDecimalFast(string s) =>
            decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    private static EngineInstrument ToEngineInstrument(DepositBondInstrumentConfig config, decimal fundNav = 0m) =>
        new(
            config.Id,
            config.Name ?? "Instrument",
            (config.Type ?? "DEPOSIT").ToUpperInvariant(),
            NormalizeCurrency(config.Currency),
            ParseDecimal(config.AnnualRatePct),
            ParseDecimal(config.StartingBalance),
            ParseNullableDecimal(config.BondUnitNominal) ?? 99m,
            config.MaturityYears ?? 5,
            string.IsNullOrWhiteSpace(config.Ticker) ? null : config.Ticker.Trim(),
            fundNav
        );

    private static EngineAllocationRule ToEngineAllocation(AllocationRule rule)
    {
        AllocationMode mode = string.Equals(rule.Mode, "AMOUNT", StringComparison.OrdinalIgnoreCase)
            ? AllocationMode.Amount
            : AllocationMode.Percent;

        return new EngineAllocationRule(
            ToEngineCondition(rule.Condition),
            mode,
            ParseDecimal(rule.DepositPct),
            ParseDecimal(rule.StocksPct),
            ParseDecimal(rule.DepositAmount),
            ParseDecimal(rule.StocksAmount),
            string.IsNullOrWhiteSpace(rule.TargetInstrumentId) ? null : rule.TargetInstrumentId.Trim());
    }

    private static EngineStockGroup ToEngineGroup(StockGroupConfig group)
    {
        GroupAllocationMode mode = string.Equals(group.Mode, "SEQUENTIAL", StringComparison.OrdinalIgnoreCase)
            ? GroupAllocationMode.Sequential
            : GroupAllocationMode.Weighted;

        List<EngineStockMember> members = (group.Members ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Symbol))
            .Select(m => new EngineStockMember(m.Symbol.Trim(), ToMemberCondition(m.Condition), m.WeightPct ?? 0m))
            .ToList();

        return new EngineStockGroup(group.WeightPct, mode, members);
    }

    private static EngineCondition ToMemberCondition(StockMemberCondition? condition)
    {
        if (condition is null)
            return new EngineCondition(ConditionType.Always);

        return (condition.Type ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "STOCK_COUNT" => new EngineCondition(ConditionType.UntilThisShares, Decimal: ParseDecimal(condition.Value)),
            "INVESTED_AMOUNT" => new EngineCondition(ConditionType.UntilThisInvested, Amount: ParseDecimal(condition.Value)),
            "DATE" => new EngineCondition(ConditionType.UntilDate, Date: ParseMonth(condition.Value)),
            _ => new EngineCondition(ConditionType.Always),
        };
    }

    private static EngineStockRule ToEngineStock(StockRule rule) =>
        new(rule.Symbol?.Trim() ?? string.Empty, ParseNullableDecimal(rule.WeightPct), ToEngineCondition(rule.Condition));

    private static EnginePlannedExpense? ToEnginePlanned(PlannedExpense expense)
    {
        DateOnly? month = ParseMonth(expense.DateMonth);

        return month is { } m ? new EnginePlannedExpense(m, ParseDecimal(expense.Amount)) : null;
    }

    private static EngineCondition ToEngineCondition(SimulationCondition? condition)
    {
        if (condition is null)
            return new EngineCondition(ConditionType.Always);

        return (condition.Type ?? "ALWAYS").Trim().ToUpperInvariant() switch
        {
            "UNTIL_DATE" => new EngineCondition(ConditionType.UntilDate, Date: ParseMonth(condition.Date)),
            "UNTIL_DEPOSIT" => new EngineCondition(ConditionType.UntilDeposit, Amount: ParseDecimal(condition.Amount)),
            "UNTIL_STOCK_COUNT" => new EngineCondition(ConditionType.UntilStockCount, Count: condition.Count ?? 0),
            "BETWEEN_DATES" => new EngineCondition(ConditionType.BetweenDates, Date: ParseMonth(condition.Date), StartDate: ParseMonth(condition.StartDate)),
            _ => new EngineCondition(ConditionType.Always),
        };
    }

    private static ShortfallPolicy ParseShortfall(string? value) =>
        string.Equals(value?.Trim(), "DEPOSIT", StringComparison.OrdinalIgnoreCase)
            ? ShortfallPolicy.Deposit
            : ShortfallPolicy.None;

    private static decimal ResolveScenario(string? scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return ModerateGrowth;

        string s = scenario.Trim();

        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal custom))
            return custom;

        return s.ToUpperInvariant() switch
        {
            "CONSERVATIVE" => ConservativeGrowth,
            "MODERATE" => ModerateGrowth,
            "HISTORICAL" => HistoricalGrowth,
            _ => ModerateGrowth,
        };
    }

    private static SavedSimulationDto Map(SavedRow row)
    {
        SimulationConfig config = DeserializeConfig(row.Config, row.HorizonYear, row.BaseCurrency);

        return new SavedSimulationDto(
            row.Id.ToString(), row.Name, row.HorizonYear, NormalizeCurrency(row.BaseCurrency),
            config, FormatTimestamp(row.CreatedAt), FormatTimestamp(row.UpdatedAt));
    }

    private static SimulationConfig DeserializeConfig(string? json, int horizonYear, string baseCurrency)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                SimulationConfig? parsed = JsonSerializer.Deserialize<SimulationConfig>(json, Json);

                if (parsed is not null)
                    return parsed;
            }
            catch (JsonException)
            {
                // fall through to an empty config below
            }
        }

        return new SimulationConfig(horizonYear, NormalizeCurrency(baseCurrency), "0", "0", "MODERATE", [], [], [], [], "NONE");
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("A simulation name is required.");

        return name.Trim();
    }

    private static DateOnly FirstOfMonth(DateTimeOffset now) => new(now.Year, now.Month, 1);

    private static DateOnly? ParseStartMonth(string? value) => ParseMonth(value);

    private static DateOnly? ParseMonth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string v = value.Trim();

        if (DateOnly.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly full))
            return new DateOnly(full.Year, full.Month, 1);

        if (DateOnly.TryParseExact(v + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly ym))
            return ym;

        return null;
    }

    private static decimal ParseDecimal(string? value) => ParseNullableDecimal(value) ?? 0m;

    private static decimal? ParseNullableDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : null;

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "RON" : currency.Trim().ToUpperInvariant();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatQty(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPct(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private sealed record PriceRow(string? Currency, decimal? Close);

    private sealed record DividendRow(string? Symbol, decimal? ForwardDividend, decimal? HistGrowthCagr);

    private sealed record SavedRow(
        Guid Id, string Name, int HorizonYear, string BaseCurrency, string? Config,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record SavedIdRow(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}

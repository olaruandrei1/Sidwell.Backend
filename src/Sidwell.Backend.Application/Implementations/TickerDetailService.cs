using System.Globalization;
using System.Text.Json;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class TickerDetailService(
    IUnitOfWork uow,
    ISettingsService settingsService,
    ILookupQueue queue,
    IFinnhubMetricsClient finnhub,
    IYfinanceMetricsClient yfinanceMetrics,
    ICoreRecalcTrigger recalcTrigger
) : ITickerDetailService
{
    private const string DividendSql = """
        SELECT dividend_yield AS "DividendYield", forward_dividend AS "ForwardDividend",
               ex_dividend_date AS "ExDividendDate", pay_frequency AS "PayFrequency",
               hist_growth_cagr AS "HistGrowthCagr"
        FROM ticker_dividends
        WHERE ticker_id = @tickerId
        """;

    private const string TickerSql = """
        SELECT id AS Id, symbol AS Symbol, name AS Name, exchange AS Exchange, currency AS Currency, sec_cik AS SecCik
        FROM tickers
        WHERE upper(symbol) = upper(@symbol)
        ORDER BY symbol
        LIMIT 1;
        """;

    private const string SearchSql = """
        SELECT symbol AS Symbol, name AS Name, exchange AS Exchange, currency AS Currency,
               country AS Country, asset_type AS AssetType
        FROM tickers
        WHERE symbol ILIKE @prefix OR name ILIKE @contains
        ORDER BY (symbol ILIKE @prefix) DESC, length(symbol), symbol
        LIMIT 20;
        """;

    private const string LatestBarSql = """
        SELECT date::text AS Date, open::text AS Open, high::text AS High, low::text AS Low, close::text AS Close, volume AS Volume
        FROM price_history
        WHERE ticker_id = @tickerId
        ORDER BY date DESC
        LIMIT 1;
        """;

    private const string HistorySql = """
        SELECT date::text AS Date, open::text AS Open, high::text AS High, low::text AS Low, close::text AS Close, volume AS Volume
        FROM price_history
        WHERE ticker_id = @tickerId AND date >= CURRENT_DATE - 1825
        ORDER BY date ASC;
        """;

    private const string CompositeSql = """
        SELECT score AS Score, details::text AS DetailsJson
        FROM algorithm_scores
        WHERE algorithm_name = 'composite' AND ticker_id = @tickerId AND philosophy = @philosophy
        ORDER BY as_of_date DESC
        LIMIT 1;
        """;

    private const string AlgorithmsSql = """
        SELECT DISTINCT ON (algorithm_name)
               algorithm_name AS Name, score AS Score, details::text AS DetailsJson
        FROM algorithm_scores
        WHERE ticker_id = @tickerId AND algorithm_name <> 'composite' AND philosophy = 'ALL'
        ORDER BY algorithm_name, as_of_date DESC;
        """;

    private const string FundamentalsSql = """
        SELECT as_of_date::text AS AsOfDate, period AS Period,
               revenue::text AS Revenue, net_income::text AS NetIncome, gross_profit::text AS GrossProfit, ebit::text AS Ebit,
               total_assets::text AS TotalAssets, total_liabilities::text AS TotalLiabilities, total_equity::text AS TotalEquity,
               eps::text AS Eps, shares_outstanding AS SharesOutstanding
        FROM fundamentals
        WHERE ticker_id = @tickerId
        ORDER BY as_of_date DESC
        LIMIT 8;
        """;

    private const string NewsSql = """
        SELECT title AS Title, url AS Url, published_at::text AS PublishedAt, sentiment::text AS Sentiment, source AS Source
        FROM news_items
        WHERE ticker_id = @tickerId
        ORDER BY published_at DESC
        LIMIT 20;
        """;

    private const string NewsCountSql = """
        SELECT COUNT(*)
        FROM news_items
        WHERE ticker_id = @tickerId;
        """;

    private const string NewsPaginatedSql = """
        SELECT title AS Title, url AS Url, published_at::text AS PublishedAt, sentiment::text AS Sentiment, source AS Source
        FROM news_items
        WHERE ticker_id = @tickerId
        ORDER BY published_at DESC
        LIMIT @limit OFFSET @offset;
        """;

    private const string HoldingSql = """
        SELECT h.shares::text AS Shares, h.avg_cost::text AS AvgCost, h.realized_pnl::text AS RealizedPnl,
               ph.close AS LatestClose, pt.target_shares::text AS TargetShares, h.broker AS Broker
        FROM holdings h
        LEFT JOIN LATERAL (SELECT close FROM price_history WHERE ticker_id = h.ticker_id ORDER BY date DESC LIMIT 1) ph ON true
        LEFT JOIN portfolio_targets pt ON pt.user_id = h.user_id AND pt.ticker_id = h.ticker_id
        WHERE h.user_id = @userId AND h.ticker_id = @tickerId;
        """;

    private const string NoteSql = "SELECT body FROM ticker_notes WHERE user_id = @userId AND ticker_id = @tickerId;";

    private const string WatchlistedSql = "SELECT EXISTS(SELECT 1 FROM watchlist WHERE user_id = @userId AND ticker_id = @tickerId);";

    private const string LatestCloseSql =
        "SELECT close FROM price_history WHERE ticker_id = @tickerId ORDER BY date DESC LIMIT 1;";

    private const string FiveYearAgoCloseSql = """
        SELECT close FROM price_history
        WHERE ticker_id = @tickerId AND date <= (CURRENT_DATE - INTERVAL '5 years')::date
        ORDER BY date DESC LIMIT 1;
        """;

    private const string UpsertNoteSql = """
        INSERT INTO ticker_notes (user_id, ticker_id, body, updated_at)
        VALUES (@userId, @tickerId, @body, now())
        ON CONFLICT (user_id, ticker_id) DO UPDATE SET body = EXCLUDED.body, updated_at = now();
        """;

    private const string UserTaxCountrySql =
        "SELECT value FROM user_settings WHERE user_id = @userId AND key = 'tax_country';";

    private const string DividendTaxRateSql =
        "SELECT rate_percent FROM dividend_tax_rates WHERE country_code = @country;";

    public async Task<TickerDetail?> GetBySymbolAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol }, ct: ct);

        if (ticker is null)
            return null;

        SettingsDto settings = await settingsService.GetAsync(userId, ct);

        Task<PriceBar?> latestTask = 
            uow.Dapper.QueryFirstOrDefaultAsync<PriceBar>(LatestBarSql, new { tickerId = ticker.Id }, ct: ct);

        Task<IReadOnlyList<PriceBar>> historyTask = 
            uow.Dapper.QueryAsync<PriceBar>(HistorySql, new { tickerId = ticker.Id }, ct: ct);

        Task<CompositeRow?> compositeTask = 
            uow.Dapper.QueryFirstOrDefaultAsync<CompositeRow>(CompositeSql, new { tickerId = ticker.Id, philosophy = settings.Philosophy }, ct: ct);

        Task<IReadOnlyList<AlgorithmRow>> algorithmsTask = 
            uow.Dapper.QueryAsync<AlgorithmRow>(AlgorithmsSql, new { tickerId = ticker.Id }, ct: ct);

        Task<IReadOnlyList<FundamentalPeriod>> fundamentalsTask = 
            uow.Dapper.QueryAsync<FundamentalPeriod>(FundamentalsSql, new { tickerId = ticker.Id }, ct: ct);

        Task<IReadOnlyList<NewsRow>> newsTask = 
            uow.Dapper.QueryAsync<NewsRow>(NewsSql, new { tickerId = ticker.Id }, ct: ct);

        Task<HoldingRow?> holdingTask = 
            uow.Dapper.QueryFirstOrDefaultAsync<HoldingRow>(HoldingSql, new { userId, tickerId = ticker.Id }, ct: ct);

        Task<string?> noteTask = 
            uow.Dapper.QueryFirstOrDefaultAsync<string?>(NoteSql, new { userId, tickerId = ticker.Id }, ct: ct);

        Task<bool> watchlistedTask = 
            uow.Dapper.ExecuteScalarAsync<bool>(WatchlistedSql, new { userId, tickerId = ticker.Id }, ct: ct);

        Task<TickerDividendRow?> dividendTask =
            uow.Dapper.QueryFirstOrDefaultAsync<TickerDividendRow>(DividendSql, new { tickerId = ticker.Id }, ct: ct);

        Task<FinnhubStockMetrics?> finnhubTask = finnhub.GetMetricsAsync(ticker.Symbol, ct);
        Task<YfinanceStockMetrics?> yfinanceTask = yfinanceMetrics.GetMetricsAsync(ticker.Symbol, ct);
        Task<decimal?> livePriceTask = yfinanceMetrics.GetLivePriceAsync(ticker.Symbol, ct);

        await Task.WhenAll(
            latestTask,
            historyTask,
            compositeTask,
            algorithmsTask,
            fundamentalsTask,
            newsTask,
            holdingTask,
            noteTask,
            watchlistedTask,
            dividendTask,
            finnhubTask,
            yfinanceTask,
            livePriceTask
        );

        CompositeRow? compositeRow = compositeTask.Result;
        IReadOnlyList<AlgorithmRow> algorithmRows = algorithmsTask.Result;

        bool hasAnyScore = algorithmRows.Any(r => r.Score is not null) || compositeRow is { Score: not null };

        // Fundamentals-less tickers (e.g. BVB) still get a technical-only composite score and
        // price-based algorithms (e.g. momentum) from NativeRecalcService, so retry recalc for
        // them too as long as we have any price history to score against — not just when SEC
        // fundamentals exist.
        if (!hasAnyScore && (fundamentalsTask.Result.Count > 0 || historyTask.Result.Count > 0))
        {
            bool recalculated = await recalcTrigger.RecalcAsync(ticker.Id, DateOnly.FromDateTime(DateTime.UtcNow), ct);

            if (recalculated)
            {
                Task<CompositeRow?> compositeRetryTask =
                    uow.Dapper.QueryFirstOrDefaultAsync<CompositeRow>(CompositeSql, new { tickerId = ticker.Id, philosophy = settings.Philosophy }, ct: ct);

                Task<IReadOnlyList<AlgorithmRow>> algorithmsRetryTask =
                    uow.Dapper.QueryAsync<AlgorithmRow>(AlgorithmsSql, new { tickerId = ticker.Id }, ct: ct);

                await Task.WhenAll(compositeRetryTask, algorithmsRetryTask);

                compositeRow = compositeRetryTask.Result;
                algorithmRows = algorithmsRetryTask.Result;
            }
        }

        CompositeScore? composite = compositeRow is { Score: not null }
            ? BuildCompositeScore(settings.Philosophy, compositeRow)
            : null;

        List<GatedAlgo> gatedAlgos = new();
        HashSet<string> gatedNames = new(StringComparer.OrdinalIgnoreCase);

        if (fundamentalsTask.Result.Count == 0)
        {
            foreach (string name in new[] { "piotroski", "altman_z", "greenblatt", "dcf", "pe_projections", "peg", "accruals", "gross_profitability", "beneish_m" })
            {
                gatedAlgos.Add(new GatedAlgo(name, "SEC fundamentals not available"));
                gatedNames.Add(name);
            }
        }

        if (dividendTask.Result is null)
        {
            gatedAlgos.Add(new GatedAlgo("ddm", "Dividend data not available"));
            gatedNames.Add("ddm");
        }

        if (historyTask.Result.Count < 10)
        {
            gatedAlgos.Add(new GatedAlgo("momentum", "Insufficient price history"));
            gatedNames.Add("momentum");
        }

        IReadOnlyList<AlgoScore> algorithms = algorithmRows
            .Where(r => !gatedNames.Contains(r.Name))
            .Select(BuildAlgoScore)
            .ToList();

        IReadOnlyList<NewsItem> news = newsTask.Result.Select(BuildNewsItem).ToList();

        HoldingDto? holding = holdingTask.Result is { } holdingRow
            ? BuildHoldingDto(new TickerSummary(ticker.Symbol, ticker.Name, ticker.Exchange, ticker.Currency), holdingRow)
            : null;

        DividendInfoDto dividends = BuildDividends(ticker.Symbol, ticker.Id, dividendTask.Result);
        KeyStatsDto keyStats = BuildKeyStats(historyTask.Result, fundamentalsTask.Result, latestTask.Result, finnhubTask.Result, yfinanceTask.Result);

        return new TickerDetail(
            new TickerDetailTicker(ticker.Symbol, ticker.Name, ticker.Exchange, ticker.Currency, ticker.SecCik),
            new TickerDetailPrice(latestTask.Result, historyTask.Result, livePriceTask.Result?.ToString("F4", CultureInfo.InvariantCulture)),
            composite,
            algorithms,
            fundamentalsTask.Result,
            news,
            holding,
            noteTask.Result,
            watchlistedTask.Result,
            dividends,
            keyStats,
            gatedAlgos
        );
    }

    private DividendInfoDto BuildDividends(string symbol, Guid tickerId, TickerDividendRow? row)
    {
        if (row is null)
        {
            queue.TryEnqueueDividend(new DividendLookupJob(symbol, tickerId, null));
            return new DividendInfoDto(null, null, null, null, null, "PENDING");
        }

        return new DividendInfoDto(
            row.DividendYield?.ToString(CultureInfo.InvariantCulture),
            row.ForwardDividend?.ToString(CultureInfo.InvariantCulture),
            row.ExDividendDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.PayFrequency,
            row.HistGrowthCagr?.ToString(CultureInfo.InvariantCulture),
            "CACHED"
        );
    }

    private static KeyStatsDto BuildKeyStats(IReadOnlyList<PriceBar> history, IReadOnlyList<FundamentalPeriod> fundamentals, PriceBar? latest, FinnhubStockMetrics? finnhub, YfinanceStockMetrics? yfinance)
    {
        DateOnly cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-365));

        decimal? minLow = null;
        decimal? maxHigh = null;

        foreach (PriceBar bar in history)
        {
            if (!DateOnly.TryParse(bar.Date, CultureInfo.InvariantCulture, out DateOnly date) || date < cutoff)
                continue;

            if (decimal.TryParse(bar.Low, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal low))
                minLow = minLow is null ? low : Math.Min(minLow.Value, low);

            if (decimal.TryParse(bar.High, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal high))
                maxHigh = maxHigh is null ? high : Math.Max(maxHigh.Value, high);
        }

        decimal? close = latest is not null && decimal.TryParse(latest.Close, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal c)
            ? c
            : null;

        string? marketCap = null;

        long? shares = fundamentals.FirstOrDefault(f => f.SharesOutstanding.HasValue)?.SharesOutstanding;

        if (close is { } price && shares is { } sharesOutstanding)
            marketCap = (price * sharesOutstanding).ToString("F2", CultureInfo.InvariantCulture);

        string? peTrailing = null;

        FundamentalPeriod? annual = fundamentals.FirstOrDefault(f => f.Period == "FY" && f.Eps is not null);

        if (close is { } px && annual?.Eps is { } epsText
            && decimal.TryParse(epsText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal eps) && eps > 0m)
            peTrailing = (px / eps).ToString("F2", CultureInfo.InvariantCulture);

        string? beta = finnhub?.Beta?.ToString("F2", CultureInfo.InvariantCulture);
        string? targetOneYear = finnhub?.TargetOneYear?.ToString("F2", CultureInfo.InvariantCulture);
        string? earningsDate = finnhub?.NextEarningsDate;
        string? priceToBook = finnhub?.PriceToBook?.ToString("F2", CultureInfo.InvariantCulture);
        string? roeTtm = finnhub?.RoeTtm?.ToString("F2", CultureInfo.InvariantCulture);
        string? debtToEquity = finnhub?.DebtToEquity?.ToString("F2", CultureInfo.InvariantCulture);
        string? revenueGrowth = finnhub?.RevenueGrowthTtmYoy?.ToString("F2", CultureInfo.InvariantCulture);
        string? evToEbitda = finnhub?.EvToEbitda?.ToString("F2", CultureInfo.InvariantCulture);

        // Fallback computations from local fundamentals when Finnhub returns null
        // (typical for BVB tickers — Finnhub coverage is US/EU-major only).
        decimal ParseOrZero(string? s) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : 0m;

        if (priceToBook is null && close is { } pxPB && annual?.TotalEquity is { } equityText
            && ParseOrZero(equityText) is decimal eq && eq > 0 && shares is { } sh && sh > 0)
        {
            decimal bookPerShare = eq / sh;
            if (bookPerShare > 0)
                priceToBook = (pxPB / bookPerShare).ToString("F2", CultureInfo.InvariantCulture);
        }

        if (roeTtm is null && annual?.NetIncome is { } niText && annual?.TotalEquity is { } eqText2)
        {
            decimal ni = ParseOrZero(niText);
            decimal eq2 = ParseOrZero(eqText2);
            if (eq2 > 0)
                roeTtm = (ni / eq2 * 100m).ToString("F2", CultureInfo.InvariantCulture);
        }

        if (debtToEquity is null && annual?.TotalLiabilities is { } liabText && annual?.TotalEquity is { } eqText3)
        {
            decimal liab = ParseOrZero(liabText);
            decimal eq3 = ParseOrZero(eqText3);
            if (eq3 > 0)
                debtToEquity = (liab / eq3).ToString("F2", CultureInfo.InvariantCulture);
        }

        if (revenueGrowth is null)
        {
            FundamentalPeriod[] annuals = fundamentals.Where(f => f.Period == "FY" && f.Revenue is not null).ToArray();
            if (annuals.Length >= 2)
            {
                decimal current = ParseOrZero(annuals[0].Revenue);
                decimal prior = ParseOrZero(annuals[1].Revenue);
                if (prior > 0)
                    revenueGrowth = ((current - prior) / prior * 100m).ToString("F2", CultureInfo.InvariantCulture);
            }
        }

        if (evToEbitda is null && marketCap is not null && annual?.Ebit is { } ebitText)
        {
            decimal ebit = ParseOrZero(ebitText);
            decimal mc = ParseOrZero(marketCap);
            if (ebit > 0 && mc > 0)
                evToEbitda = (mc / ebit).ToString("F2", CultureInfo.InvariantCulture);
        }

        // Last-resort fallback from yfinance — covers exchanges Finnhub doesn't (e.g. BVB/.RO),
        // where the local-fundamentals fallback above also has nothing to work with because
        // `fundamentals` is SEC-only and empty for those tickers.
        beta ??= yfinance?.Beta?.ToString("F2", CultureInfo.InvariantCulture);
        targetOneYear ??= yfinance?.TargetOneYear?.ToString("F2", CultureInfo.InvariantCulture);
        earningsDate ??= yfinance?.NextEarningsDate;
        peTrailing ??= yfinance?.PeTrailingTtm?.ToString("F2", CultureInfo.InvariantCulture);
        priceToBook ??= yfinance?.PriceToBook?.ToString("F2", CultureInfo.InvariantCulture);
        roeTtm ??= yfinance?.RoeTtm?.ToString("F2", CultureInfo.InvariantCulture);
        debtToEquity ??= yfinance?.DebtToEquity?.ToString("F2", CultureInfo.InvariantCulture);
        revenueGrowth ??= yfinance?.RevenueGrowthTtmYoy?.ToString("F2", CultureInfo.InvariantCulture);
        evToEbitda ??= yfinance?.EvToEbitda?.ToString("F2", CultureInfo.InvariantCulture);
        marketCap ??= yfinance?.MarketCap?.ToString("F2", CultureInfo.InvariantCulture);

        int? analystBuy = finnhub?.Consensus?.Buy ?? yfinance?.Consensus?.Buy;
        int? analystHold = finnhub?.Consensus?.Hold ?? yfinance?.Consensus?.Hold;
        int? analystSell = finnhub?.Consensus?.Sell ?? yfinance?.Consensus?.Sell;
        string? analystConsensus = null;
        if ((finnhub?.Consensus ?? yfinance?.Consensus) is { } consensus)
        {
            int total = consensus.Buy + consensus.Hold + consensus.Sell;
            if (total > 0)
            {
                double buyPct = (double)consensus.Buy / total;
                double sellPct = (double)consensus.Sell / total;
                analystConsensus = buyPct >= 0.6 ? "Buy" : sellPct >= 0.4 ? "Sell" : "Hold";
            }
        }

        return new KeyStatsDto(
            minLow?.ToString(CultureInfo.InvariantCulture),
            maxHigh?.ToString(CultureInfo.InvariantCulture),
            beta,
            peTrailing,
            marketCap,
            earningsDate,
            targetOneYear,
            priceToBook,
            roeTtm,
            debtToEquity,
            revenueGrowth,
            evToEbitda,
            analystBuy,
            analystHold,
            analystSell,
            analystConsensus
        );
    }

    public async Task<IReadOnlyList<TickerSummary>> SearchAsync(string query, CancellationToken ct = default)
    {
        string trimmed = query.Trim();

        if (trimmed.Length == 0)
            return [];

        return await uow.Dapper.QueryAsync<TickerSummary>(
            SearchSql,
            new { prefix = $"{trimmed}%", contains = $"%{trimmed}%" },
            ct: ct);
    }

    public async Task<bool> UpdateNoteAsync(Guid userId, string symbol, string body, CancellationToken ct = default)
    {
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol }, ct: ct);

        if (ticker is null)
            return false;

        await uow.Dapper.ExecuteAsync(UpsertNoteSql, new { userId, tickerId = ticker.Id, body }, ct: ct);

        return true;
    }

    public async Task<GrowthProjectionDto?> GetGrowthProjectionAsync(string symbol, decimal targetShares, CancellationToken ct = default)
    {
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol }, ct: ct);

        if (ticker is null)
            return null;

        Task<decimal?> latestCloseTask = uow.Dapper.ExecuteScalarAsync<decimal?>(LatestCloseSql, new { tickerId = ticker.Id }, ct: ct);
        Task<decimal?> fiveYearCloseTask = uow.Dapper.ExecuteScalarAsync<decimal?>(FiveYearAgoCloseSql, new { tickerId = ticker.Id }, ct: ct);

        await Task.WhenAll(latestCloseTask, fiveYearCloseTask);

        decimal? currentPrice = latestCloseTask.Result;
        decimal? oldPrice = fiveYearCloseTask.Result;

        if (currentPrice is null or <= 0m)
            return new GrowthProjectionDto([]);

        decimal? historicCagr = oldPrice is { } old && old > 0m
            ? (decimal)(Math.Pow((double)(currentPrice.Value / old), 1.0 / 5.0) - 1.0)
            : null;

        string invested = (targetShares * currentPrice.Value).ToString("F2", CultureInfo.InvariantCulture);

        List<GrowthScenario> scenarios = new();

        if (historicCagr.HasValue)
        {
            scenarios.Add(BuildGrowthScenario(
                "Historic", historicCagr.Value, currentPrice.Value, targetShares, invested));
        }

        scenarios.Add(BuildGrowthScenario("Conservative", 0.06m, currentPrice.Value, targetShares, invested));
        scenarios.Add(BuildGrowthScenario("Moderate", 0.08m, currentPrice.Value, targetShares, invested));
        scenarios.Add(BuildGrowthScenario("Aggressive", 0.10m, currentPrice.Value, targetShares, invested));

        return new GrowthProjectionDto(scenarios);
    }

    private static GrowthScenario BuildGrowthScenario(string name, decimal cagr, decimal currentPrice, decimal targetShares, string invested)
    {
        List<GrowthScenarioRow> rows = new(10);

        for (int year = 1; year <= 10; year++)
        {
            decimal projectedPrice = currentPrice * (decimal)Math.Pow(1.0 + (double)cagr, year);
            string value = (targetShares * projectedPrice).ToString("F2", CultureInfo.InvariantCulture);
            rows.Add(new GrowthScenarioRow(year, value, invested));
        }

        return new GrowthScenario(name, (cagr * 100m).ToString("F2", CultureInfo.InvariantCulture), rows);
    }

    public async Task<PaginatedResult<NewsItem>?> GetNewsPaginatedAsync(string symbol, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        string trimmed = symbol.Trim().ToUpperInvariant();
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol = trimmed }, ct: ct);
        if (ticker is null)
            return null;

        int totalCount = await uow.Dapper.ExecuteScalarAsync<int>(NewsCountSql, new { tickerId = ticker.Id }, ct: ct);
        int totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);

        int offset = (page - 1) * pageSize;
        IReadOnlyList<NewsRow> rows = await uow.Dapper.QueryAsync<NewsRow>(
            NewsPaginatedSql,
            new { tickerId = ticker.Id, limit = pageSize, offset },
            ct: ct);

        IReadOnlyList<NewsItem> items = rows.Select(BuildNewsItem).ToList();
        return new PaginatedResult<NewsItem>(items, totalCount, page, pageSize, totalPages);
    }

    public async Task<MyProjectionDto?> GetMyProjectionAsync(Guid userId, string symbol, CancellationToken ct = default)
    {
        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol }, ct: ct);
        if (ticker is null)
            return null;

        HoldingRow? holding = await uow.Dapper.QueryFirstOrDefaultAsync<HoldingRow>(HoldingSql, new { userId, tickerId = ticker.Id }, ct: ct);
        if (holding is null)
            return null;

        decimal shares = decimal.Parse(holding.Shares, CultureInfo.InvariantCulture);
        decimal avgCost = decimal.Parse(holding.AvgCost, CultureInfo.InvariantCulture);
        decimal currentPrice = holding.LatestClose ?? avgCost;
        decimal currentValue = shares * currentPrice;

        TickerDividendRow? dividendRow = await uow.Dapper.QueryFirstOrDefaultAsync<TickerDividendRow>(DividendSql, new { tickerId = ticker.Id }, ct: ct);

        decimal forwardDividend = dividendRow?.ForwardDividend ?? 0m;
        decimal taxRate = 0.16m;

        if (dividendRow is not null)
        {
            string country = await uow.Dapper.ExecuteScalarAsync<string>(UserTaxCountrySql, new { userId }, ct: ct) ?? "RO";
            decimal? taxRatePercent = await uow.Dapper.ExecuteScalarAsync<decimal?>(DividendTaxRateSql, new { country }, ct: ct);
            taxRate = (taxRatePercent ?? 16m) / 100m;
        }

        List<MyProjectionRow> projRows = new(10);
        decimal cumulativeDividends = 0m;
        decimal dps = forwardDividend;

        for (int year = 1; year <= 10; year++)
        {
            decimal projectedPrice = currentPrice * (decimal)Math.Pow(1.08, year);
            decimal projectedValue = shares * projectedPrice;

            if (dividendRow is not null && dps > 0m)
            {
                dps *= 1.06m;
                cumulativeDividends += shares * dps * (1m - taxRate);
            }

            projRows.Add(new MyProjectionRow(
                DateTime.UtcNow.Year + year,
                FormatDecimal(projectedValue)!,
                cumulativeDividends.ToString("F2", CultureInfo.InvariantCulture)
            ));
        }

        return new MyProjectionDto(holding.Shares, holding.AvgCost, FormatDecimal(currentValue)!, projRows);
    }

    public async Task<TickerLatestPriceDto> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
    {
        string trimmed = symbol.Trim().ToUpperInvariant();

        TickerRow? ticker = await uow.Dapper.QueryFirstOrDefaultAsync<TickerRow>(TickerSql, new { symbol = trimmed }, ct: ct);
        if (ticker is null)
            return new TickerLatestPriceDto(trimmed, null, "NONE", null);

        PriceBar? latest = await uow.Dapper.QueryFirstOrDefaultAsync<PriceBar>(LatestBarSql, new { tickerId = ticker.Id }, ct: ct);
        if (latest is null)
            return new TickerLatestPriceDto(ticker.Symbol, null, "NONE", null);

        return new TickerLatestPriceDto(ticker.Symbol, latest.Close, "PRICE_HISTORY", latest.Date);
    }

    private static CompositeScore BuildCompositeScore(string philosophy, CompositeRow row)
    {
        IReadOnlyDictionary<string, JsonElement> outputs = ExtractOutputs(row.DetailsJson);

        string label = outputs.TryGetValue("label", out JsonElement labelEl) ? labelEl.GetString() ?? "Mix-Feelings" : "Mix-Feelings";
        string color = outputs.TryGetValue("color", out JsonElement colorEl) ? colorEl.GetString() ?? "#EAB308" : "#EAB308";

        bool overridden = outputs.TryGetValue("overridden", out JsonElement overriddenEl) && overriddenEl.ValueKind == JsonValueKind.True;

        return new CompositeScore(philosophy, FormatDecimal(row.Score)!, label, color, overridden);
    }

    private static AlgoScore BuildAlgoScore(AlgorithmRow row)
    {
        IReadOnlyDictionary<string, JsonElement> outputs = ExtractOutputs(row.DetailsJson);

        bool applicable = row.Score is not null;

        Dictionary<string, object?>? details = outputs.Count == 0
            ? null
            : outputs.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        return new AlgoScore(row.Name, FormatDecimal(row.Score), applicable, details);
    }

    private static NewsItem BuildNewsItem(NewsRow row)
    {
        string source = row.Source ?? string.Empty;

        if (string.IsNullOrWhiteSpace(source) && Uri.TryCreate(row.Url, UriKind.Absolute, out Uri? parsed))
            source = parsed.Host;

        return new NewsItem(row.Title, row.Url, row.PublishedAt, row.Sentiment, source);
    }

    private static HoldingDto BuildHoldingDto(TickerSummary ticker, HoldingRow row)
    {
        decimal shares = decimal.Parse(row.Shares, CultureInfo.InvariantCulture);
        decimal avgCost = decimal.Parse(row.AvgCost, CultureInfo.InvariantCulture);
        decimal? latestClose = row.LatestClose;

        decimal marketValue = shares * (latestClose ?? avgCost);
        decimal unrealizedPnl = latestClose.HasValue ? (latestClose.Value - avgCost) * shares : 0m;

        bool targetReached = row.TargetShares is not null
            && decimal.TryParse(row.TargetShares, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal targetVal)
            && shares >= targetVal;

        return new HoldingDto(
            ticker,
            row.Shares,
            row.AvgCost,
            ticker.Currency,
            FormatDecimal(marketValue)!,
            FormatDecimal(unrealizedPnl)!,
            row.RealizedPnl,
            row.TargetShares,
            targetReached,
            row.Broker ?? "TradeVille"
        );
    }

    private static IReadOnlyDictionary<string, JsonElement> ExtractOutputs(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return new Dictionary<string, JsonElement>();

        using JsonDocument document = JsonDocument.Parse(detailsJson);

        if (!document.RootElement.TryGetProperty("outputs", out JsonElement outputsElement) || outputsElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        Dictionary<string, JsonElement> result = new();

        foreach (JsonProperty property in outputsElement.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static string? FormatDecimal(decimal? value) => value?.ToString("0.000000", CultureInfo.InvariantCulture);

    private sealed record TickerRow(Guid Id, string Symbol, string Name, string Exchange, string Currency, string? SecCik);

    private sealed record CompositeRow(decimal? Score, string? DetailsJson);

    private sealed record AlgorithmRow(string Name, decimal? Score, string? DetailsJson);

    private sealed record NewsRow(string Title, string Url, string PublishedAt, string? Sentiment, string? Source);

    private sealed record HoldingRow(string Shares, string AvgCost, string RealizedPnl, decimal? LatestClose, string? TargetShares, string? Broker);

    private sealed record TickerDividendRow(
        decimal? DividendYield,
        decimal? ForwardDividend,
        DateOnly? ExDividendDate,
        string? PayFrequency,
        decimal? HistGrowthCagr
    );
}

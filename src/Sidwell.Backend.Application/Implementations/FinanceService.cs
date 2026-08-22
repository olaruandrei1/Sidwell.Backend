using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class FinanceService(
    IUnitOfWork uow,
    IReceiptImageProcessor receiptImageProcessor,
    IGeminiClient gemini,
    ITransactionService transactionService
) : IFinanceService
{
    private const string LatestPriceForSymbolSql = """
        SELECT ph.close AS Close, ph.date AS Date, t.symbol AS Symbol, t.currency AS Currency
        FROM tickers t
        JOIN price_history ph ON ph.ticker_id = t.id
        WHERE upper(t.symbol) = upper(@symbol)
        ORDER BY ph.date DESC
        LIMIT 1;
        """;

    private const string LatestRateToRonSql =
        "SELECT rate_to_ron FROM exchange_rates WHERE currency = @currency ORDER BY rate_date DESC LIMIT 1;";

    private const string AllLatestRatesToRonSql = """
        SELECT currency AS Currency, rate_to_ron AS RateToRon
        FROM (
            SELECT currency, rate_to_ron,
                   ROW_NUMBER() OVER (PARTITION BY currency ORDER BY rate_date DESC) as rn
            FROM exchange_rates
        ) sub
        WHERE rn = 1;
        """;

    private const string BrokerIdleCashSumSql = """
        SELECT COALESCE(SUM(amount), 0)
        FROM wealth_allocations
        WHERE user_id = @userId
          AND institution = @institution
          AND institution_type = 'BROKER'
          AND currency = @currency;
        """;

    // Reconstruct holdings-as-of end-of-@month per (ticker, broker) from transactions.
    // avg_cost = Σ (BUY shares × price + fee) / Σ BUY shares (weighted; ignores SELLs for cost basis snapshot).
    // Latest close from price_history at-or-before end-of-month is used for market value.
    private const string HoldingsAsOfMonthSql = """
        WITH month_end AS (
            SELECT (to_date(@month || '-01', 'YYYY-MM-DD') + INTERVAL '1 month' - INTERVAL '1 day')::date AS asof
        )
        SELECT t.symbol AS Symbol,
               t.name AS Name,
               t.exchange AS Exchange,
               t.currency AS Currency,
               COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares
                                 WHEN upper(tr.side) = 'SELL' THEN -tr.shares
                                 ELSE 0 END), 0) AS Shares,
               CASE
                   WHEN COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares ELSE 0 END), 0) > 0
                   THEN COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares * tr.price + COALESCE(tr.fee, 0) ELSE 0 END), 0)
                        / SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares ELSE 0 END)
                   ELSE 0
               END AS AvgCost,
               COALESCE((
                   SELECT ph.close FROM price_history ph
                   WHERE ph.ticker_id = t.id AND ph.date <= (SELECT asof FROM month_end)
                   ORDER BY ph.date DESC LIMIT 1
               ), 0) AS Close,
               tr.broker AS Broker
        FROM transactions tr
        JOIN tickers t ON t.id = tr.ticker_id
        WHERE tr.user_id = @userId
          AND tr.executed_at::date <= (SELECT asof FROM month_end)
        GROUP BY t.id, t.symbol, t.name, t.exchange, t.currency, tr.broker
        HAVING COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares
                                 WHEN upper(tr.side) = 'SELL' THEN -tr.shares
                                 ELSE 0 END), 0) > 0.00000001
        ORDER BY t.symbol, tr.broker;
        """;

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] CategoryTypes =
        ["LOAN", "SUBSCRIPTION", "UTILITY", "VARIABLE", "FOOD", "CIGARETTES", "OTHER"];

    private static readonly string[] ExpenseStatuses = ["PAID", "DUE", "PENDING"];

    private static readonly string[] InstitutionTypes = ["BANK", "BROKER"];

    private static readonly string[] WealthTypes = ["BANK_DEPOSIT", "BROKER_CASH", "DCA_TARGET"];

    private static readonly (string Name, string Type)[] DefaultCategories =
    [
        ("Loan", "LOAN"),
        ("Subscription", "SUBSCRIPTION"),
        ("Utility", "UTILITY"),
        ("Variable", "VARIABLE"),
        ("Food", "FOOD"),
        ("Cigarettes", "CIGARETTES"),
        ("Other", "OTHER"),
    ];

    private const string SelectSettingsSql = """
        SELECT monthly_income_amount AS MonthlyIncomeAmount,
               monthly_income_currency AS MonthlyIncomeCurrency,
               banks::text AS Banks,
               brokers::text AS Brokers,
               category_types::text AS CategoryTypes
        FROM finance_settings
        WHERE user_id = @userId;
        """;

    private const string UpsertSettingsSql = """
        INSERT INTO finance_settings (user_id, monthly_income_amount, monthly_income_currency, banks, brokers, category_types, updated_at)
        VALUES (@userId, @amount, @currency, @banks::jsonb, @brokers::jsonb, @categoryTypes::jsonb, now())
        ON CONFLICT (user_id) DO UPDATE SET
            monthly_income_amount = EXCLUDED.monthly_income_amount,
            monthly_income_currency = EXCLUDED.monthly_income_currency,
            banks = EXCLUDED.banks,
            brokers = EXCLUDED.brokers,
            category_types = EXCLUDED.category_types,
            updated_at = now();
        """;

    private const string SelectCategoryTypesJsonSql =
        "SELECT category_types::text FROM finance_settings WHERE user_id = @userId;";

    private const string SelectCategoriesSql = """
        SELECT id AS Id, name AS Name, type AS Type, is_default AS IsDefault
        FROM finance_categories
        WHERE user_id = @userId
        ORDER BY type, name;
        """;

    private const string InsertCategorySql = """
        INSERT INTO finance_categories (id, user_id, name, type, is_default)
        VALUES (@id, @userId, @name, @type, @isDefault)
        ON CONFLICT (user_id, name, type) DO NOTHING;
        """;

    private const string DeleteCategoriesSql = "DELETE FROM finance_categories WHERE user_id = @userId;";

    private const string SelectExpensesSql = """
        SELECT e.id AS Id, e.month AS Month, e.name AS Name, e.category AS Category, e.amount AS Amount,
               e.currency AS Currency, e.type AS Type,
               COALESCE(o.status, CASE WHEN e.month = @month THEN e.status ELSE 'DUE' END) AS Status,
               e.due_date AS DueDate,
               e.interest_rate_pct AS InterestRatePct, e.is_recurring AS IsRecurring, e.created_at AS CreatedAt
        FROM expenses e
        LEFT JOIN expense_status_overrides o
            ON o.user_id = e.user_id AND o.expense_id = e.id AND o.month = @month
        WHERE e.user_id = @userId
          AND (
              e.month = @month
              OR (
                  e.is_recurring = true
                  AND e.month < @month
                  AND NOT EXISTS (
                      SELECT 1 FROM expenses e2
                      WHERE e2.user_id = e.user_id
                        AND lower(e2.name) = lower(e.name)
                        AND e2.type = e.type
                        AND (e2.month = @month OR (e2.month > e.month AND e2.month <= @month AND e2.is_recurring = true))
                  )
              )
          )
        ORDER BY e.created_at DESC;
        """;

    private const string UpsertStatusOverrideSql = """
        INSERT INTO expense_status_overrides (user_id, expense_id, month, status, updated_at)
        VALUES (@userId, @expenseId, @month, @status, now())
        ON CONFLICT (user_id, expense_id, month) DO UPDATE
            SET status = EXCLUDED.status,
                updated_at = now();
        """;

    private const string SelectExpenseCoreSql = """
        SELECT id AS Id, month AS Month, name AS Name, category AS Category, amount AS Amount,
               currency AS Currency, type AS Type, status AS Status, due_date AS DueDate,
               interest_rate_pct AS InterestRatePct, is_recurring AS IsRecurring, created_at AS CreatedAt
        FROM expenses
        WHERE id = @id AND user_id = @userId;
        """;

    private const string InsertExpenseSql = """
        INSERT INTO expenses
            (user_id, month, name, category, amount, currency, type, status, due_date, interest_rate_pct, is_recurring, line_items)
        VALUES
            (@userId, @month, @name, @category, @amount, @currency, @type, @status, @dueDate, @interestRatePct, @isRecurring, @lineItemsJson::jsonb)
        RETURNING id AS Id, month AS Month, name AS Name, category AS Category, amount AS Amount,
                  currency AS Currency, type AS Type, status AS Status, due_date AS DueDate,
                  interest_rate_pct AS InterestRatePct, is_recurring AS IsRecurring, created_at AS CreatedAt;
        """;

    private const string SelectExpenseByIdSql = """
        SELECT id AS Id, month AS Month, name AS Name, category AS Category, amount AS Amount,
               currency AS Currency, type AS Type, status AS Status, due_date AS DueDate,
               interest_rate_pct AS InterestRatePct, is_recurring AS IsRecurring, created_at AS CreatedAt,
               line_items::text AS LineItemsJson
        FROM expenses
        WHERE id = @expenseId AND user_id = @userId;
        """;

    private const string UpdateExpenseStatusSql = """
        UPDATE expenses SET status = @status
        WHERE id = @id AND user_id = @userId
        RETURNING id AS Id, month AS Month, name AS Name, category AS Category, amount AS Amount,
                  currency AS Currency, type AS Type, status AS Status, due_date AS DueDate,
                  interest_rate_pct AS InterestRatePct, is_recurring AS IsRecurring, created_at AS CreatedAt;
        """;

    private const string UpdateExpenseSql = """
        UPDATE expenses
        SET month = @month,
            name = @name,
            category = @category,
            amount = @amount,
            currency = @currency,
            type = @type,
            status = @status,
            due_date = @dueDate,
            interest_rate_pct = @interestRatePct,
            is_recurring = @isRecurring,
            line_items = @lineItemsJson::jsonb
        WHERE id = @id AND user_id = @userId
        RETURNING id AS Id, month AS Month, name AS Name, category AS Category, amount AS Amount,
                  currency AS Currency, type AS Type, status AS Status, due_date AS DueDate,
                  interest_rate_pct AS InterestRatePct, is_recurring AS IsRecurring, created_at AS CreatedAt;
        """;

    private const string DeleteExpenseSql = "DELETE FROM expenses WHERE id = @id AND user_id = @userId;";

    private const string SelectExpenseSeriesRangeSql = """
        WITH pivot AS (
            SELECT name, category, type, amount, currency
            FROM expenses
            WHERE id = @expenseId AND user_id = @userId
        )
        SELECT MIN(e.month) AS StartMonth, MAX(e.month) AS EndMonth, COUNT(*)::int AS Count
        FROM expenses e
        JOIN pivot p
          ON e.name = p.name
         AND e.category = p.category
         AND e.type = p.type
         AND e.amount = p.amount
         AND e.currency = p.currency
        WHERE e.user_id = @userId;
        """;

    private const string SelectWealthSql = """
        SELECT id AS Id, month AS Month, name AS Name, institution AS Institution, institution_type AS InstitutionType,
               type AS Type, amount AS Amount, currency AS Currency, interest_rate_pct AS InterestRatePct,
               notes AS Notes, created_at AS CreatedAt
        FROM wealth_allocations
        WHERE user_id = @userId AND month = @month
        ORDER BY created_at DESC;
        """;

    // Cumulative wealth: one row per (institution, type, currency, name) — i.e. per distinct account.
    // Same-name repeat contributions across months are summed into that account's total.
    // Different names at same institution stay separate so the UI shows one card per account.
    // HAVING SUM > 0 hides fully-drained accounts + phantom withdrawal-only rows (Retragere: …).
    private const string SelectCumulativeWealthSql = """
        SELECT MIN(id::text)::uuid AS Id,
               MAX(month) AS Month,
               (array_agg(name ORDER BY created_at ASC))[1] AS Name,
               institution AS Institution,
               institution_type AS InstitutionType,
               type AS Type,
               SUM(amount) AS Amount,
               currency AS Currency,
               (array_agg(interest_rate_pct ORDER BY created_at ASC))[1] AS InterestRatePct,
               (array_agg(notes ORDER BY created_at ASC))[1] AS Notes,
               MIN(created_at) AS CreatedAt
        FROM wealth_allocations
        WHERE user_id = @userId AND month <= @month
        GROUP BY institution, institution_type, currency, type
        HAVING SUM(amount) > 0
        ORDER BY institution, type, currency;
        """;

    // Sub-items per bucket for grey list in Patrimoniu — one row per distinct name inside a bucket.
    private const string SelectCumulativeWealthDetailsSql = """
        SELECT institution AS Institution,
               institution_type AS InstitutionType,
               currency AS Currency,
               type AS Type,
               name AS Name,
               SUM(amount) AS Amount
        FROM wealth_allocations
        WHERE user_id = @userId AND month <= @month
        GROUP BY institution, institution_type, currency, type, name
        ORDER BY institution, type, currency, name;
        """;

    // Unlike SelectCumulativeWealthSql (which HAVING-filters out negative-only "phantom" buckets so
    // standalone withdrawals don't render as their own account card), this has no per-name grouping
    // or HAVING — every deposit and withdrawal counts, so the total actually reflects reality.
    private const string SelectWealthTotalByCurrencyAndTypeSql = """
        SELECT currency AS Currency, institution_type AS InstitutionType, COALESCE(SUM(amount), 0) AS Total
        FROM wealth_allocations
        WHERE user_id = @userId AND month <= @month
        GROUP BY currency, institution_type;
        """;

    private const string SelectNetInvestedByCurrencySql = """
        WITH month_end AS (
            SELECT (to_date(@month || '-01', 'YYYY-MM-DD') + INTERVAL '1 month' - INTERVAL '1 day')::date AS asof
        )
        SELECT t.currency AS Currency,
               COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares * tr.price + COALESCE(tr.fee, 0)
                                 WHEN upper(tr.side) = 'SELL' THEN -(tr.shares * tr.price - COALESCE(tr.fee, 0))
                                 ELSE 0 END), 0) AS NetInvested
        FROM transactions tr
        JOIN tickers t ON t.id = tr.ticker_id
        WHERE tr.user_id = @userId
          AND tr.executed_at::date <= (SELECT asof FROM month_end)
        GROUP BY t.currency;
        """;

    // Net capital invested per broker, per currency, up to end-of-@month — the cumulative
    // BUY-minus-SELL cash flow that actually left/entered the account via that broker.
    // This is distinct from wealth_allocations (idle-cash contributions) and drives the
    // broker card total on the Real Wealth Snapshot.
    private const string SelectBrokerNetInvestedSql = """
        WITH month_end AS (
            SELECT (to_date(@month || '-01', 'YYYY-MM-DD') + INTERVAL '1 month' - INTERVAL '1 day')::date AS asof
        )
        SELECT tr.broker AS Broker,
               t.currency AS Currency,
               COALESCE(SUM(CASE WHEN upper(tr.side) = 'BUY' THEN tr.shares * tr.price + COALESCE(tr.fee, 0)
                                 WHEN upper(tr.side) = 'SELL' THEN -(tr.shares * tr.price - COALESCE(tr.fee, 0))
                                 ELSE 0 END), 0) AS NetInvested
        FROM transactions tr
        JOIN tickers t ON t.id = tr.ticker_id
        WHERE tr.user_id = @userId
          AND tr.executed_at::date <= (SELECT asof FROM month_end)
        GROUP BY tr.broker, t.currency
        ORDER BY tr.broker, t.currency;
        """;

    // All open user holdings with today's latest close price — used for parallel per-ticker PnL analysis.
    private const string SelectHoldingsWithTodayPriceSql = """
        SELECT t.symbol AS Symbol,
               (t.currency)::text AS Currency,
               h.shares AS Shares,
               h.avg_cost AS AvgCost,
               COALESCE((
                   SELECT ph.close FROM price_history ph
                   WHERE ph.ticker_id = h.ticker_id
                   ORDER BY ph.date DESC LIMIT 1
               ), 0) AS TodayClose
        FROM holdings h
        JOIN tickers t ON t.id = h.ticker_id
        WHERE h.user_id = @userId
          AND h.shares > 0.00000001
        ORDER BY t.symbol;
        """;

    private const string SelectPriorWealthMonthSql = """
        SELECT month
        FROM wealth_allocations
        WHERE user_id = @userId AND month < @month
        ORDER BY month DESC
        LIMIT 1;
        """;

    private const string CopyWealthFromPriorMonthSql = """
        INSERT INTO wealth_allocations
            (user_id, month, name, institution, institution_type, type, amount, currency, interest_rate_pct, notes)
        SELECT user_id, @month, name, institution, institution_type, type, amount, currency, interest_rate_pct, notes
        FROM wealth_allocations
        WHERE user_id = @userId AND month = @priorMonth;
        """;

    private const string InsertWealthSql = """
        INSERT INTO wealth_allocations
            (user_id, month, name, institution, institution_type, type, amount, currency, interest_rate_pct, notes)
        VALUES
            (@userId, @month, @name, @institution, @institutionType, @type, @amount, @currency, @interestRatePct, @notes)
        RETURNING id AS Id, month AS Month, name AS Name, institution AS Institution, institution_type AS InstitutionType,
                  type AS Type, amount AS Amount, currency AS Currency, interest_rate_pct AS InterestRatePct,
                  notes AS Notes, created_at AS CreatedAt;
        """;

    private const string UpdateWealthSql = """
        UPDATE wealth_allocations
        SET name = @name,
            institution = @institution,
            institution_type = @institutionType,
            type = @type,
            amount = @amount,
            currency = @currency,
            interest_rate_pct = @interestRatePct,
            notes = @notes
        WHERE id = @id AND user_id = @userId
        RETURNING id AS Id, month AS Month, name AS Name, institution AS Institution, institution_type AS InstitutionType,
                  type AS Type, amount AS Amount, currency AS Currency, interest_rate_pct AS InterestRatePct,
                  notes AS Notes, created_at AS CreatedAt;
        """;

    private const string DeleteWealthSql = "DELETE FROM wealth_allocations WHERE id = @id AND user_id = @userId;";

    private const string SelectExtraIncomesSql = """
        SELECT id AS Id, month AS Month, name AS Name, amount AS Amount,
               currency AS Currency, notes AS Notes, created_at AS CreatedAt
        FROM extra_incomes
        WHERE user_id = @userId AND month = @month
        ORDER BY created_at DESC;
        """;

    private const string InsertExtraIncomeSql = """
        INSERT INTO extra_incomes (user_id, month, name, amount, currency, notes)
        VALUES (@userId, @month, @name, @amount, @currency, @notes)
        RETURNING id AS Id, month AS Month, name AS Name, amount AS Amount,
                  currency AS Currency, notes AS Notes, created_at AS CreatedAt;
        """;

    private const string DeleteExtraIncomeSql = "DELETE FROM extra_incomes WHERE id = @id AND user_id = @userId;";

    public async Task<FinanceSettingsDto> GetSettingsAsync(Guid userId, CancellationToken ct = default)
    {
        SettingsRow? settings = await uow.Dapper.QueryFirstOrDefaultAsync<SettingsRow>(SelectSettingsSql, new { userId }, ct);

        IReadOnlyList<CategoryRow> categories = await uow.Dapper.QueryAsync<CategoryRow>(SelectCategoriesSql, new { userId }, ct);

        if (categories.Count == 0)
        {
            foreach ((string name, string type) in DefaultCategories)
            {
                await uow.Dapper.ExecuteAsync(
                    InsertCategorySql,
                    new { id = Guid.NewGuid(), userId, name, type, isDefault = true },
                    ct);
            }

            categories = await uow.Dapper.QueryAsync<CategoryRow>(SelectCategoriesSql, new { userId }, ct);
        }

        return BuildSettings(settings, categories);
    }

    public async Task<FinanceSettingsDto> UpdateSettingsAsync(Guid userId, FinanceSettingsDto settings, CancellationToken ct = default)
    {
        decimal amount = ParseDecimal(settings.MonthlyIncome?.Amount) ?? 0m;

        string currency = NormalizeCurrency(settings.MonthlyIncome?.Currency);
        string banksJson = JsonSerializer.Serialize(settings.Banks ?? []);
        string brokersJson = JsonSerializer.Serialize(settings.Brokers ?? []);
        IReadOnlyList<FinanceCategoryTypeDef> customTypes = settings.CategoryTypes ?? [];
        string categoryTypesJson = JsonSerializer.Serialize(customTypes);

        await uow.Dapper.ExecuteAsync(
            UpsertSettingsSql,
            new { userId, amount, currency, banks = banksJson, brokers = brokersJson, categoryTypes = categoryTypesJson },
            ct
        );

        await uow.Dapper.ExecuteAsync(DeleteCategoriesSql, new { userId }, ct);

        HashSet<string> validTypeCodes = new(CategoryTypes, StringComparer.OrdinalIgnoreCase);
        foreach (FinanceCategoryTypeDef customType in customTypes)
            validTypeCodes.Add(customType.Code.Trim().ToUpperInvariant());

        foreach (FinanceCategoryDef category in settings.Categories ?? [])
        {
            string type = NormalizeCategoryType(category.Type, validTypeCodes);
            Guid id = Guid.TryParse(category.Id, out Guid parsed) ? parsed : Guid.NewGuid();

            await uow.Dapper.ExecuteAsync(
                InsertCategorySql,
                new { id, userId, name = category.Name, type, isDefault = category.IsDefault },
                ct);
        }

        return await GetSettingsAsync(userId, ct);
    }

    public async Task<MonthlyFinancesResponse> GetMonthlyAsync(Guid userId, string month, CancellationToken ct = default)
    {
        string normalizedMonth = NormalizeMonth(month);

        FinanceSettingsDto settings = await GetSettingsAsync(userId, ct);

        IReadOnlyList<ExpenseRow> expenseRows = await uow.Dapper.QueryAsync<ExpenseRow>(SelectExpensesSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<WealthRow> wealthRows = await uow.Dapper.QueryAsync<WealthRow>(SelectWealthSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<WealthRow> cumulativeWealthRows = await uow.Dapper.QueryAsync<WealthRow>(SelectCumulativeWealthSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<NetInvestedRow> netInvestedRows = await uow.Dapper.QueryAsync<NetInvestedRow>(
            SelectNetInvestedByCurrencySql, new { userId, month = normalizedMonth }, ct);

        Dictionary<string, decimal> investedByCurrency = netInvestedRows
            .ToDictionary(r => (r.Currency ?? "USD").Trim(), r => r.NetInvested, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<WealthTotalRow> wealthTotalRows = await uow.Dapper.QueryAsync<WealthTotalRow>(
            SelectWealthTotalByCurrencyAndTypeSql, new { userId, month = normalizedMonth }, ct);

        // Computed from the untouched investedByCurrency snapshot, before the cumulativeWealth loop
        // below mutates it while netting broker cash against invested capital bucket-by-bucket.
        Dictionary<string, decimal> trueTotalByCurrency = new(StringComparer.OrdinalIgnoreCase);
        foreach (WealthTotalRow r in wealthTotalRows)
        {
            string curr = r.Currency.Trim();
            decimal contribution = r.Total;
            if (string.Equals(r.InstitutionType, "BROKER", StringComparison.OrdinalIgnoreCase)
                && investedByCurrency.TryGetValue(curr, out decimal invested) && invested > 0)
            {
                contribution = Math.Max(0m, r.Total - invested);
            }
            trueTotalByCurrency[curr] = trueTotalByCurrency.GetValueOrDefault(curr) + contribution;
        }
        IReadOnlyList<CurrencyAmountDto> wealthTotalByCurrency = trueTotalByCurrency
            .Select(kvp => new CurrencyAmountDto(kvp.Key, FormatMoney(kvp.Value)))
            .ToList();

        IReadOnlyList<ExpenseItemDto> expenses = expenseRows.Select(BuildExpense).ToList();
        IReadOnlyList<WealthAllocationDto> wealth = wealthRows.Select(BuildWealth).ToList();
        IReadOnlyList<WealthAllocationDto> cumulativeWealth = cumulativeWealthRows.Select(row =>
        {
            decimal amount = row.Amount;
            if (string.Equals(row.InstitutionType, "BROKER", StringComparison.OrdinalIgnoreCase))
            {
                string curr = row.Currency.Trim();
                if (investedByCurrency.TryGetValue(curr, out decimal invested) && invested > 0)
                {
                    decimal used = Math.Min(amount, invested);
                    amount = Math.Max(0m, amount - used);
                    investedByCurrency[curr] = invested - used;
                }
            }

            return BuildWealth(row with { Amount = amount });
        }).ToList();

        IReadOnlyList<HoldingAsOfRow> holdingRows = await uow.Dapper.QueryAsync<HoldingAsOfRow>(
            HoldingsAsOfMonthSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<HoldingAsOfDto> holdingsAsOfMonth = holdingRows.Select(h => new HoldingAsOfDto(
            Symbol: h.Symbol,
            Name: h.Name ?? h.Symbol,
            Exchange: h.Exchange ?? "",
            Currency: (h.Currency ?? "USD").Trim(),
            Shares: h.Shares.ToString("0.########", CultureInfo.InvariantCulture),
            AvgCost: h.AvgCost.ToString("0.######", CultureInfo.InvariantCulture),
            // Fall back to cost basis when price history hasn't caught up (h.Close == 0), so
            // downstream P&L math doesn't surface a fake -cost "loss" for the missing quote.
            MarketValue: (h.Shares * (h.Close > 0 ? h.Close : h.AvgCost)).ToString("0.00", CultureInfo.InvariantCulture),
            Broker: h.Broker ?? "TradeVille"
        )).ToList();

        Dictionary<string, decimal> ratesToRon = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RON"] = 1m
        };

        IReadOnlyList<ExchangeRateRow> rateRows = await uow.Dapper.QueryAsync<ExchangeRateRow>(
            AllLatestRatesToRonSql, null, ct);

        foreach (ExchangeRateRow r in rateRows)
        {
            if (r.RateToRon > 0m)
                ratesToRon[r.Currency.Trim()] = r.RateToRon;
        }

        IReadOnlyList<ExtraIncomeRow> extraRows = await uow.Dapper.QueryAsync<ExtraIncomeRow>(
            SelectExtraIncomesSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<ExtraIncomeDto> extraIncomes = extraRows.Select(BuildExtraIncome).ToList();

        IReadOnlyList<BrokerNetInvestedRow> brokerNetInvestedRows = await uow.Dapper.QueryAsync<BrokerNetInvestedRow>(
            SelectBrokerNetInvestedSql, new { userId, month = normalizedMonth }, ct);

        IReadOnlyList<BrokerNetInvestedDto> brokerNetInvested = brokerNetInvestedRows.Select(r => new BrokerNetInvestedDto(
            Broker: string.IsNullOrWhiteSpace(r.Broker) ? "TradeVille" : r.Broker.Trim(),
            Currency: (r.Currency ?? "USD").Trim(),
            Amount: FormatMoney(r.NetInvested)
        )).ToList();

        MonthlyFinanceSummaryDto summary = BuildSummary(normalizedMonth, settings, expenseRows, wealthRows, extraRows, ratesToRon, brokerNetInvested);

        // Per-ticker PnL vs today's price — parallel analysis across all open positions.
        IReadOnlyList<HoldingWithPriceRow> holdingsWithPrice = await uow.Dapper.QueryAsync<HoldingWithPriceRow>(
            SelectHoldingsWithTodayPriceSql, new { userId }, ct);

        var pnlByCurrency = new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(holdingsWithPrice, ct, (h, _) =>
        {
            if (h.TodayClose <= 0m) return ValueTask.CompletedTask;
            decimal pnl = (h.TodayClose - h.AvgCost) * h.Shares;
            pnlByCurrency.AddOrUpdate(
                (h.Currency ?? "RON").Trim(),
                pnl,
                (_, existing) => existing + pnl);
            return ValueTask.CompletedTask;
        });

        IReadOnlyList<PortfolioPnlEntryDto> todayPortfolioPnl = pnlByCurrency
            .Select(kvp => new PortfolioPnlEntryDto(kvp.Key, FormatMoney(kvp.Value)))
            .ToList();

        return new MonthlyFinancesResponse(summary, expenses, wealth, settings, cumulativeWealth, holdingsAsOfMonth, extraIncomes, todayPortfolioPnl, wealthTotalByCurrency);
    }

    public async Task<ExtraIncomeDto> AddExtraIncomeAsync(Guid userId, AddExtraIncomeCommand command, CancellationToken ct = default)
    {
        string month = string.IsNullOrWhiteSpace(command.Month) ? CurrentMonth() : command.Month.Trim();

        var parameters = new
        {
            userId,
            month,
            name = string.IsNullOrWhiteSpace(command.Name) ? "Venit ocazional" : command.Name.Trim(),
            amount = ParseDecimal(command.Amount) ?? 0m,
            currency = NormalizeCurrency(command.Currency),
            notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
        };

        ExtraIncomeRow row = await uow.Dapper.QueryFirstOrDefaultAsync<ExtraIncomeRow>(InsertExtraIncomeSql, parameters, ct)
            ?? throw new InvalidOperationException("Extra income insert did not return a row.");

        return BuildExtraIncome(row);
    }

    public Task DeleteExtraIncomeAsync(Guid userId, Guid extraIncomeId, CancellationToken ct = default) =>
        uow.Dapper.ExecuteAsync(DeleteExtraIncomeSql, new { id = extraIncomeId, userId }, ct);

    public async Task<ExpenseItemDto> AddExpenseAsync(Guid userId, AddExpenseCommand command, CancellationToken ct = default)
    {
        HashSet<string> validTypeCodes = await GetValidCategoryTypeCodesAsync(userId, ct);
        string type = NormalizeCategoryType(command.Type, validTypeCodes);
        string status = NormalizeStatus(command.Status);
        string month = string.IsNullOrWhiteSpace(command.Month) ? CurrentMonth() : command.Month.Trim();

        bool isRecurring = command.IsRecurring ?? (type is "LOAN" or "SUBSCRIPTION");

        string? lineItemsJson = command.LineItems is { Count: > 0 }
            ? JsonSerializer.Serialize(command.LineItems)
            : null;

        var parameters = new
        {
            userId,
            month,
            name = string.IsNullOrWhiteSpace(command.Name) ? "Expense" : command.Name.Trim(),
            category = string.IsNullOrWhiteSpace(command.Category) ? "Other" : command.Category.Trim(),
            amount = ParseDecimal(command.Amount) ?? 0m,
            currency = NormalizeCurrency(command.Currency),
            type,
            status,
            dueDate = ParseDate(command.DueDate),
            interestRatePct = ParseDecimal(command.InterestRatePct),
            isRecurring,
            lineItemsJson,
        };

        ExpenseRow row = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseRow>(InsertExpenseSql, parameters, ct)
            ?? throw new InvalidOperationException("Expense insert did not return a row.");

        if (command.PaymentSources is { Count: > 0 })
        {
            foreach (PaymentSourceEntry src in command.PaymentSources)
            {
                decimal sourceAmount = ParseDecimal(src.Amount) ?? parameters.amount;
                if (sourceAmount <= 0) continue;

                if (!string.IsNullOrWhiteSpace(src.PositionSymbol))
                {
                    string brokerName = string.IsNullOrWhiteSpace(src.Institution) ? "TradeVille" : src.Institution.Trim();
                    await SellFromPositionAsync(userId, src.PositionSymbol.Trim(), sourceAmount, row.Id, month, brokerName, ct);
                }
                else if (!string.IsNullOrWhiteSpace(src.Institution))
                {
                    var withdrawalParams = new
                    {
                        userId,
                        month,
                        name = $"Retragere: {parameters.name}",
                        institution = src.Institution.Trim(),
                        institutionType = NormalizeInstitutionType(src.InstitutionType),
                        type = NormalizeWealthType(src.Type),
                        amount = -sourceAmount,
                        currency = NormalizeCurrency(src.Currency ?? parameters.currency),
                        interestRatePct = (decimal?)null,
                        notes = (string?)$"linked to expense {row.Id}",
                    };

                    await uow.Dapper.ExecuteAsync(InsertWealthSql, withdrawalParams, ct);
                }
            }
        }

        return BuildExpense(row) with { LineItems = command.LineItems };
    }

    private async Task SellFromPositionAsync(Guid userId, string symbol, decimal expenseAmount, Guid expenseId, string month, string brokerName, CancellationToken ct)
    {
        PriceRow? priceRow = await uow.Dapper.QueryFirstOrDefaultAsync<PriceRow>(
            LatestPriceForSymbolSql, new { symbol }, ct);

        if (priceRow is null || priceRow.Close <= 0)
            throw new ValidationException($"No live price available for {symbol}. Cannot execute SELL from position.");

        string currency = priceRow.Currency.Trim();

        // Step 1: drain idle cash at the same broker + currency first.
        decimal idleCash = await uow.Dapper.ExecuteScalarAsync<decimal>(
            BrokerIdleCashSumSql, new { userId, institution = brokerName, currency }, ct);

        decimal idleUsed = Math.Min(Math.Max(idleCash, 0m), expenseAmount);
        decimal remaining = expenseAmount - idleUsed;

        if (idleUsed > 0.005m)
        {
            var drainIdleParams = new
            {
                userId,
                month,
                name = $"Drenaj idle {brokerName} pentru cheltuială",
                institution = brokerName,
                institutionType = "BROKER",
                type = "BROKER_CASH",
                amount = -idleUsed,
                currency,
                interestRatePct = (decimal?)null,
                notes = (string?)$"used {idleUsed:0.##} idle before SELL, linked to expense {expenseId}",
            };
            await uow.Dapper.ExecuteAsync(InsertWealthSql, drainIdleParams, ct);
        }

        if (remaining <= 0.005m)
            return; // idle covered the whole expense — no SELL needed

        // Step 2: whole shares only (ceil) to cover what's left after idle.
        decimal wholeShares = Math.Ceiling(remaining / priceRow.Close);

        if (wholeShares <= 0)
            throw new ValidationException("Computed shares to sell is zero or negative.");

        decimal proceeds = wholeShares * priceRow.Close;
        decimal excessCash = proceeds - remaining;

        TransactionInput input = new(
            Symbol: priceRow.Symbol,
            Side: "SELL",
            Shares: wholeShares.ToString(CultureInfo.InvariantCulture),
            Price: priceRow.Close.ToString(CultureInfo.InvariantCulture),
            PriceAuto: true,
            Fee: null,
            ExecutedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            FxRateAtExecution: null,
            TargetShares: null,
            Broker: brokerName
        );

        await transactionService.CreateAsync(userId, input, ct);

        // Excess from ceiling → idle at broker (positive wealth entry).
        if (excessCash > 0.005m)
        {
            var idleCashParams = new
            {
                userId,
                month,
                name = $"Rest SELL {priceRow.Symbol} ({wholeShares} @ {priceRow.Close:0.####})",
                institution = brokerName,
                institutionType = "BROKER",
                type = "BROKER_CASH",
                amount = excessCash,
                currency,
                interestRatePct = (decimal?)null,
                notes = (string?)$"idle from expense {expenseId} SELL",
            };

            await uow.Dapper.ExecuteAsync(InsertWealthSql, idleCashParams, ct);
        }
    }

    public async Task<ExpenseItemDto> UpdateExpenseAsync(Guid userId, Guid expenseId, AddExpenseCommand command, CancellationToken ct = default)
    {
        HashSet<string> validTypeCodes = await GetValidCategoryTypeCodesAsync(userId, ct);
        string type = NormalizeCategoryType(command.Type, validTypeCodes);
        string status = NormalizeStatus(command.Status);
        string month = string.IsNullOrWhiteSpace(command.Month) ? CurrentMonth() : command.Month.Trim();
        bool isRecurring = command.IsRecurring ?? (type is "LOAN" or "SUBSCRIPTION");

        ExpenseRow? existing = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseRow>(
            SelectExpenseCoreSql,
            new { id = expenseId, userId },
            ct
        );

        if (existing is null)
            throw new NotFoundException("Expense not found.");

        if (existing.IsRecurring && !string.Equals(existing.Month.Trim(), month, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(command.RecurringEditScope, "ONLY_THIS_MONTH", StringComparison.OrdinalIgnoreCase))
            {
                return await AddExpenseAsync(userId, command with { IsRecurring = false, Month = month, RecurringEditScope = null }, ct);
            }

            if (string.Equals(command.RecurringEditScope, "THIS_AND_FUTURE", StringComparison.OrdinalIgnoreCase))
            {
                return await AddExpenseAsync(userId, command with { IsRecurring = true, Month = month, RecurringEditScope = null }, ct);
            }
        }

        string? lineItemsJson = command.LineItems is { Count: > 0 }
            ? JsonSerializer.Serialize(command.LineItems)
            : null;

        var parameters = new
        {
            id = expenseId,
            userId,
            month,
            name = string.IsNullOrWhiteSpace(command.Name) ? "Expense" : command.Name.Trim(),
            category = string.IsNullOrWhiteSpace(command.Category) ? "Other" : command.Category.Trim(),
            amount = ParseDecimal(command.Amount) ?? 0m,
            currency = NormalizeCurrency(command.Currency),
            type,
            status,
            dueDate = ParseDate(command.DueDate),
            interestRatePct = ParseDecimal(command.InterestRatePct),
            isRecurring,
            lineItemsJson,
        };

        ExpenseRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseRow>(UpdateExpenseSql, parameters, ct);

        if (row is null)
            throw new NotFoundException("Expense not found.");

        return BuildExpense(row) with { LineItems = command.LineItems };
    }

    public async Task<ExpenseItemDto> UpdateExpenseStatusAsync(Guid userId, Guid expenseId, string status, string? month, CancellationToken ct = default)
    {
        string normalizedStatus = NormalizeStatus(status);

        ExpenseRow? existing = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseRow>(
            SelectExpenseCoreSql,
            new { id = expenseId, userId },
            ct
        );

        if (existing is null)
            throw new NotFoundException("Expense not found.");

        if (existing.IsRecurring)
        {
            string targetMonth = string.IsNullOrWhiteSpace(month) ? existing.Month.Trim() : month.Trim();

            await uow.Dapper.ExecuteAsync(
                UpsertStatusOverrideSql,
                new { userId, expenseId, month = targetMonth, status = normalizedStatus },
                ct
            );

            return BuildExpense(existing) with { Status = normalizedStatus, Month = targetMonth };
        }

        ExpenseRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseRow>(
            UpdateExpenseStatusSql,
            new { id = expenseId, userId, status = normalizedStatus },
            ct
        );

        if (row is null)
            throw new NotFoundException("Expense not found.");

        return BuildExpense(row);
    }

    public Task DeleteExpenseAsync(Guid userId, Guid expenseId, CancellationToken ct = default) =>
        uow.Dapper.ExecuteAsync(DeleteExpenseSql, new { id = expenseId, userId }, ct);

    public async Task<WealthAllocationDto> AddWealthAllocationAsync(Guid userId, AddWealthAllocationCommand command, CancellationToken ct = default)
    {
        string month = string.IsNullOrWhiteSpace(command.Month) ? CurrentMonth() : command.Month.Trim();

        var parameters = new
        {
            userId,
            month,
            name = string.IsNullOrWhiteSpace(command.Name) ? "Allocation" : command.Name.Trim(),
            institution = string.IsNullOrWhiteSpace(command.Institution) ? "Unknown" : command.Institution.Trim(),
            institutionType = NormalizeInstitutionType(command.InstitutionType),
            type = NormalizeWealthType(command.Type),
            amount = ParseDecimal(command.Amount) ?? 0m,
            currency = NormalizeCurrency(command.Currency),
            interestRatePct = ParseDecimal(command.InterestRatePct),
            notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
        };

        WealthRow row = await uow.Dapper.QueryFirstOrDefaultAsync<WealthRow>(InsertWealthSql, parameters, ct)
            ?? throw new InvalidOperationException("Wealth allocation insert did not return a row.");

        return BuildWealth(row);
    }

    public async Task<WealthAllocationDto> UpdateWealthAllocationAsync(Guid userId, Guid allocationId, AddWealthAllocationCommand command, CancellationToken ct = default)
    {
        var parameters = new
        {
            id = allocationId,
            userId,
            name = string.IsNullOrWhiteSpace(command.Name) ? "Allocation" : command.Name.Trim(),
            institution = string.IsNullOrWhiteSpace(command.Institution) ? "Unknown" : command.Institution.Trim(),
            institutionType = NormalizeInstitutionType(command.InstitutionType),
            type = NormalizeWealthType(command.Type),
            amount = ParseDecimal(command.Amount) ?? 0m,
            currency = NormalizeCurrency(command.Currency),
            interestRatePct = ParseDecimal(command.InterestRatePct),
            notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
        };

        WealthRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<WealthRow>(UpdateWealthSql, parameters, ct);

        if (row is null)
            throw new NotFoundException("Wealth allocation not found.");

        return BuildWealth(row);
    }

    public Task DeleteWealthAllocationAsync(Guid userId, Guid allocationId, CancellationToken ct = default) =>
        uow.Dapper.ExecuteAsync(DeleteWealthSql, new { id = allocationId, userId }, ct);

    public async Task<WealthSnapshotPreviewDto> GetWealthSnapshotPreviewAsync(Guid userId, string month, CancellationToken ct = default)
    {
        string normalizedMonth = NormalizeMonth(month);

        IReadOnlyList<WealthRow> current = await uow.Dapper.QueryAsync<WealthRow>(SelectWealthSql, new { userId, month = normalizedMonth }, ct);

        if (current.Count > 0)
            return new WealthSnapshotPreviewDto(Available: false, PriorMonth: null, Count: 0, Total: "0.00");

        string? priorMonth = await uow.Dapper.QueryFirstOrDefaultAsync<string?>(
            SelectPriorWealthMonthSql, new { userId, month = normalizedMonth }, ct);

        if (string.IsNullOrWhiteSpace(priorMonth))
            return new WealthSnapshotPreviewDto(Available: false, PriorMonth: null, Count: 0, Total: "0.00");

        IReadOnlyList<WealthRow> priorRows = await uow.Dapper.QueryAsync<WealthRow>(SelectWealthSql, new { userId, month = priorMonth.Trim() }, ct);

        decimal total = priorRows.Sum(r => r.Amount);

        return new WealthSnapshotPreviewDto(
            Available: true,
            PriorMonth: priorMonth.Trim(),
            Count: priorRows.Count,
            Total: FormatMoney(total)
        );
    }

    public async Task<int> SnapshotWealthFromPriorMonthAsync(Guid userId, string month, CancellationToken ct = default)
    {
        string normalizedMonth = NormalizeMonth(month);

        string? priorMonth = await uow.Dapper.QueryFirstOrDefaultAsync<string?>(
            SelectPriorWealthMonthSql, new { userId, month = normalizedMonth }, ct);

        if (string.IsNullOrWhiteSpace(priorMonth))
            return 0;

        return await uow.Dapper.ExecuteAsync(
            CopyWealthFromPriorMonthSql,
            new { userId, month = normalizedMonth, priorMonth = priorMonth.Trim() },
            ct);
    }

    public async Task<ExpenseItemDto?> GetExpenseByIdAsync(Guid userId, Guid expenseId, CancellationToken ct = default)
    {
        ExpenseDetailRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<ExpenseDetailRow>(
            SelectExpenseByIdSql,
            new { expenseId, userId },
            ct
        );

        if (row is null)
            return null;

        IReadOnlyList<ExpenseLineItemDto>? lineItems = null;

        if (!string.IsNullOrWhiteSpace(row.LineItemsJson))
        {
            try
            {
                lineItems = JsonSerializer.Deserialize<List<ExpenseLineItemDto>>(row.LineItemsJson, JsonReadOptions);
            }
            catch (JsonException)
            {
                lineItems = null;
            }
        }

        return new ExpenseItemDto(
            Id: row.Id.ToString(),
            Name: row.Name,
            Category: row.Category,
            Amount: FormatMoney(row.Amount),
            Currency: row.Currency.Trim(),
            Type: row.Type,
            Status: row.Status,
            DueDate: row.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            InterestRatePct: row.InterestRatePct.HasValue ? FormatPercent(row.InterestRatePct.Value) : null,
            CreatedAt: FormatTimestamp(row.CreatedAt),
            Month: row.Month.Trim(),
            IsRecurring: row.IsRecurring,
            LineItems: lineItems
        );
    }

    public async Task<ExpenseSeriesRangeDto?> GetExpenseSeriesRangeAsync(Guid userId, Guid expenseId, CancellationToken ct = default)
    {
        SeriesRangeRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<SeriesRangeRow>(
            SelectExpenseSeriesRangeSql,
            new { expenseId, userId },
            ct
        );

        if (row is null || row.Count == 0 || string.IsNullOrWhiteSpace(row.StartMonth))
            return null;

        return new ExpenseSeriesRangeDto(row.StartMonth.Trim(), (row.EndMonth ?? row.StartMonth).Trim(), row.Count);
    }

    private sealed record SeriesRangeRow(string? StartMonth, string? EndMonth, int Count);

    public async Task<ExpenseItemDto?> ScanReceiptAsync(Guid userId, Stream imageStream, string? contentType, CancellationToken ct = default)
    {
        ProcessedReceiptImage processed;

        try
        {
            processed = await receiptImageProcessor.ProcessAndStoreAsync(imageStream, userId, ct);
        }
        catch (Exception)
        {
            return null;
        }

        GeminiReceiptResult? result = await gemini.ParseReceiptAsync(processed.Bytes, processed.MimeType, ct);

        if (result is null)
            return null;

        string category = string.IsNullOrWhiteSpace(result.Category) ? "Other" : result.Category.Trim();
        string merchant = string.IsNullOrWhiteSpace(result.Merchant) ? "Receipt" : result.Merchant.Trim();

        IReadOnlyList<ExpenseLineItemDto>? lineItems = result.Items?.Count > 0
            ? result.Items.Select(i => new ExpenseLineItemDto(
                i.Name ?? string.Empty,
                i.Qty ?? 1,
                FormatMoney(i.UnitPrice ?? 0m),
                FormatMoney(i.Amount ?? 0m)
            )).ToList()
            : null;

        return new ExpenseItemDto(
            Id: string.Empty,
            Name: $"[AI] {merchant}",
            Category: category,
            Amount: FormatMoney(result.Total ?? 0m),
            Currency: "RON",
            Type: InferType(category),
            Status: "PENDING",
            DueDate: null,
            InterestRatePct: null,
            CreatedAt: FormatTimestamp(DateTimeOffset.UtcNow),
            Month: CurrentMonth(),
            IsRecurring: false,
            LineItems: lineItems
        );
    }

    private static FinanceSettingsDto BuildSettings(SettingsRow? settings, IReadOnlyList<CategoryRow> categories)
    {
        string amount = FormatMoney(settings?.MonthlyIncomeAmount ?? 0m);
        string currency = NormalizeCurrency(settings?.MonthlyIncomeCurrency);

        IReadOnlyList<string> banks = ParseStringArray(settings?.Banks);
        IReadOnlyList<string> brokers = ParseStringArray(settings?.Brokers);
        IReadOnlyList<FinanceCategoryTypeDef> categoryTypes = ParseCategoryTypes(settings?.CategoryTypes);

        IReadOnlyList<FinanceCategoryDef> categoryDefs = categories
            .Select(c => new FinanceCategoryDef(c.Id.ToString(), c.Name, c.Type, c.IsDefault))
            .ToList();

        return new FinanceSettingsDto(new MonthlyIncomeDto(amount, currency), categoryDefs, banks, brokers, categoryTypes);
    }

    private static MonthlyFinanceSummaryDto BuildSummary(
        string month,
        FinanceSettingsDto settings,
        IReadOnlyList<ExpenseRow> expenses,
        IReadOnlyList<WealthRow> wealth,
        IReadOnlyList<ExtraIncomeRow> extras,
        IReadOnlyDictionary<string, decimal> ratesToRon,
        IReadOnlyList<BrokerNetInvestedDto> brokerNetInvested
    )
    {
        decimal baseIncome = ParseDecimal(settings.MonthlyIncome.Amount) ?? 0m;
        string currency = NormalizeCurrency(settings.MonthlyIncome.Currency);

        decimal totalExtraIncomes = extras.Sum(x => ConvertCurrency(x.Amount, x.Currency, currency, ratesToRon));
        decimal netIncome = baseIncome + totalExtraIncomes;

        decimal loansAndSubs = 0m, utilities = 0m, variable = 0m;

        foreach (ExpenseRow e in expenses)
        {
            decimal convertedAmount = ConvertCurrency(e.Amount, e.Currency, currency, ratesToRon);

            if (e.Type is "LOAN" or "SUBSCRIPTION")
                loansAndSubs += convertedAmount;
            else if (e.Type is "UTILITY")
                utilities += convertedAmount;
            else
                variable += convertedAmount;
        }

        decimal totalExpenses = loansAndSubs + utilities + variable;
        decimal wealthWithdrawals = wealth
            .Where(w => w.Amount < 0m)
            .Sum(w => ConvertCurrency(w.Amount, w.Currency, currency, ratesToRon));

        decimal outOfPocketExpenses = Math.Max(0m, totalExpenses + wealthWithdrawals);
        decimal positiveAllocatedWealth = wealth
            .Where(w => w.Amount > 0m)
            .Sum(w => ConvertCurrency(w.Amount, w.Currency, currency, ratesToRon));
        decimal freeCash = netIncome - outOfPocketExpenses - positiveAllocatedWealth;

        decimal savingsRate = netIncome > 0m
            ? Math.Clamp(freeCash / netIncome * 100m, 0m, 100m)
            : 0m;

        string? netIncomeInRon = null;
        string? exchangeRate = null;
        string? totalExtraIncomesInRon = null;

        if (!string.Equals(currency, "RON", StringComparison.OrdinalIgnoreCase) &&
            ratesToRon.TryGetValue(currency, out decimal rateToRon) && rateToRon > 0m)
        {
            netIncomeInRon = FormatMoney(netIncome * rateToRon);
            exchangeRate = rateToRon.ToString("0.####", CultureInfo.InvariantCulture);
            // Extras were converted into `currency` above; reproject them into RON so the
            // frontend can show a coherent RON figure when the user's display currency is RON.
            totalExtraIncomesInRon = FormatMoney(extras.Sum(x => ConvertCurrency(x.Amount, x.Currency, "RON", ratesToRon)));
        }

        return new MonthlyFinanceSummaryDto(
            Month: month,
            NetIncome: FormatMoney(netIncome),
            Currency: currency,
            NetIncomeInRon: netIncomeInRon,
            ExchangeRate: exchangeRate,
            TotalLoansAndSubs: FormatMoney(loansAndSubs),
            TotalUtilities: FormatMoney(utilities),
            TotalVariableExpenses: FormatMoney(variable),
            TotalExpenses: FormatMoney(totalExpenses),
            TotalAllocatedWealth: FormatMoney(positiveAllocatedWealth),
            FreeCash: FormatMoney(freeCash),
            SavingsRatePct: savingsRate.ToString("0.0", CultureInfo.InvariantCulture),
            TotalExtraIncomes: FormatMoney(totalExtraIncomes),
            TotalExtraIncomesInRon: totalExtraIncomesInRon,
            BrokerNetInvested: brokerNetInvested
        );
    }

    private static ExtraIncomeDto BuildExtraIncome(ExtraIncomeRow row) => new(
        Id: row.Id.ToString(),
        Month: row.Month.Trim(),
        Name: row.Name,
        Amount: FormatMoney(row.Amount),
        Currency: row.Currency.Trim(),
        Notes: row.Notes,
        CreatedAt: FormatTimestamp(row.CreatedAt)
    );

    private static ExpenseItemDto BuildExpense(ExpenseRow row) => new(
        Id: row.Id.ToString(),
        Name: row.Name,
        Category: row.Category,
        Amount: FormatMoney(row.Amount),
        Currency: row.Currency.Trim(),
        Type: row.Type,
        Status: row.Status,
        DueDate: row.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        InterestRatePct: row.InterestRatePct.HasValue ? FormatPercent(row.InterestRatePct.Value) : null,
        CreatedAt: FormatTimestamp(row.CreatedAt),
        Month: row.Month.Trim(),
        IsRecurring: row.IsRecurring
    );

    private static WealthAllocationDto BuildWealth(WealthRow row) => new(
        Id: row.Id.ToString(),
        Name: row.Name,
        Institution: row.Institution,
        InstitutionType: row.InstitutionType,
        Type: row.Type,
        Amount: FormatMoney(row.Amount),
        Currency: row.Currency.Trim(),
        InterestRatePct: row.InterestRatePct.HasValue ? FormatPercent(row.InterestRatePct.Value) : null,
        Notes: row.Notes,
        Month: row.Month.Trim()
    );

    private static string InferType(string category)
    {
        string c = category.ToLowerInvariant();

        if (c.Contains("food") || c.Contains("grocer") || c.Contains("supermarket") || c.Contains("restaurant"))
            return "FOOD";
        if (c.Contains("cigar") || c.Contains("tobacco") || c.Contains("tutun") || c.Contains("tigar"))
            return "CIGARETTES";
        if (c.Contains("util") || c.Contains("electric") || c.Contains("gas") || c.Contains("water"))
            return "UTILITY";
        if (c.Contains("subscription") || c.Contains("abonament"))
            return "SUBSCRIPTION";
        if (c.Contains("loan") || c.Contains("credit"))
            return "LOAN";

        return "OTHER";
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<FinanceCategoryTypeDef> ParseCategoryTypes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<FinanceCategoryTypeDef>>(json, JsonReadOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<HashSet<string>> GetValidCategoryTypeCodesAsync(Guid userId, CancellationToken ct)
    {
        string? json = await uow.Dapper.ExecuteScalarAsync<string?>(SelectCategoryTypesJsonSql, new { userId }, ct);
        HashSet<string> codes = new(CategoryTypes, StringComparer.OrdinalIgnoreCase);
        foreach (FinanceCategoryTypeDef def in ParseCategoryTypes(json))
            codes.Add(def.Code.Trim().ToUpperInvariant());
        return codes;
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed) ? parsed : null;

    private static string FormatMoney(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPercent(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string CurrentMonth() => DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string NormalizeMonth(string? month) =>
        string.IsNullOrWhiteSpace(month) ? CurrentMonth() : month.Trim();

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "RON" : currency.Trim().ToUpperInvariant();

    private static string NormalizeCategoryType(string? type, IReadOnlySet<string> validCodes)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "OTHER";

        string upper = type.Trim().ToUpperInvariant();
        return validCodes.Contains(upper) ? upper : "OTHER";
    }

    private static string NormalizeStatus(string? status) => Normalize(status, ExpenseStatuses, "PAID");

    private static string NormalizeInstitutionType(string? type) => Normalize(type, InstitutionTypes, "BANK");

    private static string NormalizeWealthType(string? type) => Normalize(type, WealthTypes, "BANK_DEPOSIT");

    private static string Normalize(string? value, string[] allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string upper = value.Trim().ToUpperInvariant();

        return Array.IndexOf(allowed, upper) >= 0 ? upper : fallback;
    }

    private static decimal ConvertCurrency(decimal amount, string? fromCurrency, string? toCurrency, IReadOnlyDictionary<string, decimal> ratesToRon)
    {
        string from = NormalizeCurrency(fromCurrency);
        string to = NormalizeCurrency(toCurrency);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return amount;

        decimal fromRate = ratesToRon.TryGetValue(from, out decimal fr) && fr > 0m ? fr : 1m;
        decimal toRate = ratesToRon.TryGetValue(to, out decimal tr) && tr > 0m ? tr : 1m;

        return (amount * fromRate) / toRate;
    }

    private sealed record SettingsRow(decimal MonthlyIncomeAmount, string MonthlyIncomeCurrency, string? Banks, string? Brokers, string? CategoryTypes);

    private sealed record ExchangeRateRow(string Currency, decimal RateToRon);

    private sealed record CategoryRow(Guid Id, string Name, string Type, bool IsDefault);

    private sealed record ExpenseRow(
        Guid Id,
        string Month,
        string Name,
        string Category,
        decimal Amount,
        string Currency,
        string Type,
        string Status,
        DateOnly? DueDate,
        decimal? InterestRatePct,
        bool IsRecurring,
        DateTimeOffset CreatedAt
    );

    private sealed record WealthRow(
        Guid Id,
        string Month,
        string Name,
        string Institution,
        string InstitutionType,
        string Type,
        decimal Amount,
        string Currency,
        decimal? InterestRatePct,
        string? Notes,
        DateTimeOffset CreatedAt
    );

    private sealed record WealthTotalRow(string Currency, string InstitutionType, decimal Total);

    private sealed record WealthDetailRow(
        string Institution,
        string InstitutionType,
        string Currency,
        string Type,
        string Name,
        decimal Amount
    );

    private sealed record PriceRow(decimal Close, DateOnly Date, string Symbol, string Currency);

    private sealed record HoldingAsOfRow(string Symbol, string? Name, string? Exchange, string? Currency, decimal Shares, decimal AvgCost, decimal Close, string? Broker);

    private sealed record ExpenseDetailRow(
        Guid Id,
        string Month,
        string Name,
        string Category,
        decimal Amount,
        string Currency,
        string Type,
        string Status,
        DateOnly? DueDate,
        decimal? InterestRatePct,
        bool IsRecurring,
        DateTimeOffset CreatedAt,
        string? LineItemsJson
    );

    private sealed record NetInvestedRow(string Currency, decimal NetInvested);

    private sealed record BrokerNetInvestedRow(string? Broker, string Currency, decimal NetInvested);

    private sealed record HoldingWithPriceRow(string Symbol, string? Currency, decimal Shares, decimal AvgCost, decimal TodayClose);

    private sealed record ExtraIncomeRow(
        Guid Id,
        string Month,
        string Name,
        decimal Amount,
        string Currency,
        string? Notes,
        DateTimeOffset CreatedAt
    );
}

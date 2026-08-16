namespace Sidwell.Backend.Application.Implementations;

public enum ConditionType { Always, UntilDate, UntilDeposit, UntilStockCount, UntilThisShares, UntilThisInvested, BetweenDates }

public enum AllocationMode { Percent, Amount }

public enum ShortfallPolicy { None, Deposit }

public enum GroupAllocationMode { Weighted, Sequential }

public sealed record EngineCondition(
    ConditionType Type,
    DateOnly? Date = null,
    decimal? Amount = null,
    int? Count = null,
    decimal? Decimal = null,
    DateOnly? StartDate = null
);

public sealed record EngineAllocationRule(
    EngineCondition Condition,
    AllocationMode Mode,
    decimal DepositPct,
    decimal StocksPct,
    decimal DepositAmount,
    decimal StocksAmount,
    string? TargetInstrumentId = null
);

public sealed record EngineStockRule(string Symbol, decimal? WeightPct, EngineCondition Condition);

public sealed record EngineStockMember(string Symbol, EngineCondition Condition, decimal WeightPct = 0m);

public sealed record EngineStockGroup(
    decimal WeightPct,
    GroupAllocationMode Mode,
    IReadOnlyList<EngineStockMember> Members
);

public sealed record EnginePlannedExpense(DateOnly Month, decimal Amount);

public sealed record EngineInstrument(
    string Id,
    string Name,
    string Type, // "DEPOSIT" | "BOND" | "FUND"
    string Currency, // "RON" | "EUR" | "USD" | "GBP"
    decimal AnnualRatePct,
    decimal StartingBalance,
    decimal BondUnitNominal = 99m,
    int MaturityYears = 5,
    string? Ticker = null,
    decimal FundNav = 0m
);

public sealed record EngineInstrumentSnapshot(
    string Id,
    string Name,
    string Type,
    string Currency,
    decimal Balance,
    decimal InterestEarned,
    decimal BalanceInBaseCurrency,
    decimal Units = 0m,
    decimal Nav = 0m
);

public sealed record EngineInput(
    DateOnly Start,
    int HorizonYear,
    decimal MonthlyIncome,
    decimal FixedMonthlyExpense,
    decimal StartingDeposit,
    decimal DepositAnnualRatePct,
    decimal StockAnnualGrowthPct,
    IReadOnlyList<EngineAllocationRule> AllocationRules,
    IReadOnlyList<EngineStockRule> StockRules,
    IReadOnlyList<EnginePlannedExpense> PlannedExpenses,
    IReadOnlyDictionary<string, decimal> StartingPrices,
    IReadOnlyDictionary<string, decimal> StartingShares,
    ShortfallPolicy Shortfall,
    IReadOnlyList<EngineStockGroup>? StockGroups = null,
    IReadOnlyDictionary<string, decimal>? StartingInvested = null,
    IReadOnlyDictionary<string, decimal>? DividendPerShare = null,
    IReadOnlyDictionary<string, decimal>? DividendGrowthRate = null,
    decimal DividendTaxRate = 0m,
    bool ReinvestDividends = false,
    IReadOnlyList<EngineInstrument>? Instruments = null,
    IReadOnlyDictionary<string, decimal>? FxRates = null,
    string BaseCurrency = "RON"
);

public sealed record EngineRow(
    DateOnly Month,
    decimal Income,
    decimal Expenses,
    decimal ToDeposit,
    decimal ToStocks,
    decimal DepositInterest,
    decimal DepositBalance,
    decimal StockValue,
    decimal NetWorth,
    IReadOnlyDictionary<string, decimal> PerStockInvested,
    IReadOnlyDictionary<string, decimal> PerStockDividends,
    IReadOnlyDictionary<string, decimal> PerStockValue,
    IReadOnlyDictionary<string, decimal> PerStockShares,
    IReadOnlyList<EngineInstrumentSnapshot>? PerInstrument = null
);

public sealed record EngineSummary(
    decimal FinalNetWorth,
    decimal FinalDeposit,
    decimal FinalPortfolio,
    decimal TotalInvested,
    decimal TotalToDeposit,
    decimal TotalInterest,
    decimal TotalIncome,
    decimal TotalExpenses,
    decimal TotalStockCapitalGains = 0m,
    decimal TotalDepositInterest = 0m,
    decimal TotalBondCoupons = 0m,
    IReadOnlyDictionary<string, decimal>? CurrencyBreakdown = null,
    IReadOnlyDictionary<string, decimal>? MarketExposure = null,
    IReadOnlyDictionary<string, decimal>? NetWorthByCurrency = null,
    IReadOnlyDictionary<string, decimal>? StockValueByMarket = null
);

public sealed record EngineResult(
    IReadOnlyList<EngineRow> Rows,
    EngineSummary Summary,
    IReadOnlyDictionary<string, decimal> FinalShares,
    IReadOnlyDictionary<string, decimal> FinalInvestedPerSymbol,
    IReadOnlyDictionary<string, decimal> TotalDividendsPerSymbol
);

public static class FinanceSimulationEngine
{
    private static readonly Dictionary<string, decimal> Empty = new(StringComparer.OrdinalIgnoreCase);

    public static EngineResult Run(EngineInput input)
    {
        decimal deposit = input.StartingDeposit;
        Dictionary<string, decimal> shares = new(input.StartingShares, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> investedPerSymbol = input.StartingInvested is null
            ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, decimal>(input.StartingInvested, StringComparer.OrdinalIgnoreCase);

        bool useInstruments = input.Instruments is { Count: > 0 };
        Dictionary<string, decimal> instrumentBalances = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> instrumentInterestTotal = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> fundUnits = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, decimal> fundNav = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> instrumentCurrency = new(StringComparer.OrdinalIgnoreCase);
        string? primaryDepositId = null;

        IReadOnlyDictionary<string, decimal> fxRates = input.FxRates ?? new Dictionary<string, decimal>();
        string baseCurrency = input.BaseCurrency;

        if (useInstruments)
        {
            deposit = 0m;
            foreach (EngineInstrument inst in input.Instruments!)
            {
                instrumentBalances[inst.Id] = inst.StartingBalance;
                instrumentInterestTotal[inst.Id] = 0m;
                instrumentCurrency[inst.Id] = inst.Currency;

                if (string.Equals(inst.Type, "FUND", StringComparison.OrdinalIgnoreCase))
                {
                    decimal nav = inst.FundNav > 0m ? inst.FundNav : 100m;
                    fundNav[inst.Id] = nav;
                    fundUnits[inst.Id] = nav > 0m ? inst.StartingBalance / nav : 0m;
                }

                deposit += ToBase(inst.StartingBalance, inst.Currency, baseCurrency, fxRates);
                if (primaryDepositId is null && string.Equals(inst.Type, "DEPOSIT", StringComparison.OrdinalIgnoreCase))
                    primaryDepositId = inst.Id;
            }
            primaryDepositId ??= input.Instruments![0].Id;
        }

        decimal monthlyDepRate = useInstruments ? 0m : input.DepositAnnualRatePct / 100m / 12m;
        decimal monthlyStockRate = input.StockAnnualGrowthPct / 100m / 12m;

        DateOnly start = new(input.Start.Year, input.Start.Month, 1);
        DateOnly end = new(Math.Max(input.HorizonYear, start.Year), 12, 1);

        List<EngineRow> rows = [];
        decimal totalInvested = 0m, totalToDeposit = 0m, totalInterest = 0m, totalIncome = 0m, totalExpenses = 0m;
        Dictionary<string, decimal> totalDividendsPerSymbol = new(StringComparer.OrdinalIgnoreCase);

        decimal priceFactor = 1m;

        for (DateOnly month = start; month <= end; month = month.AddMonths(1))
        {
            int distinctStocks = CountDistinctStocks(shares);
            int yearIndex = month.Year - start.Year;

            decimal planned = 0m;
            foreach (EnginePlannedExpense pe in input.PlannedExpenses)
                if (pe.Month.Year == month.Year && pe.Month.Month == month.Month)
                    planned += pe.Amount;

            decimal monthExpenses = input.FixedMonthlyExpense + planned;
            decimal surplus = input.MonthlyIncome - monthExpenses;

            decimal interest;
            if (useInstruments)
            {
                interest = 0m;
                foreach (EngineInstrument inst in input.Instruments!)
                {
                    if (string.Equals(inst.Type, "FUND", StringComparison.OrdinalIgnoreCase))
                    {
                        decimal rate = inst.AnnualRatePct / 100m / 12m;
                        decimal navCur = fundNav.GetValueOrDefault(inst.Id);
                        decimal newNav = navCur * (1m + rate);
                        fundNav[inst.Id] = newNav;
                        decimal units = fundUnits.GetValueOrDefault(inst.Id);
                        decimal oldBal = instrumentBalances.GetValueOrDefault(inst.Id);
                        decimal newBal = units * newNav;
                        decimal gain = newBal - oldBal;
                        instrumentBalances[inst.Id] = newBal;
                        instrumentInterestTotal[inst.Id] += gain;
                        interest += ToBase(gain, inst.Currency, baseCurrency, fxRates);
                    }
                    else
                    {
                        decimal bal = instrumentBalances.GetValueOrDefault(inst.Id);
                        decimal rate = inst.AnnualRatePct / 100m / 12m;
                        decimal instInt = bal * rate;
                        instrumentBalances[inst.Id] = bal + instInt;
                        instrumentInterestTotal[inst.Id] += instInt;
                        interest += ToBase(instInt, inst.Currency, baseCurrency, fxRates);
                    }
                }
                deposit += interest;
            }
            else
            {
                interest = deposit * monthlyDepRate;
                deposit += interest;
            }
            totalInterest += interest;

            decimal toDeposit = 0m, toStocks = 0m;
            Dictionary<string, decimal> perStockInvested = new(StringComparer.OrdinalIgnoreCase);

            if (surplus >= 0m)
            {
                EngineAllocationRule? rule = FirstActive(input.AllocationRules, month, deposit, distinctStocks, instrumentBalances);

                if (rule is not null)
                    (toDeposit, toStocks) = Split(rule, surplus);

                decimal invested = BuyStocks(
                    input, shares, investedPerSymbol, perStockInvested,
                    toStocks, month, distinctStocks, deposit, priceFactor);

                totalInvested += invested;

                decimal uninvestedStocks = Math.Max(0m, toStocks - invested);
                if (uninvestedStocks > 0m)
                {
                    toDeposit += uninvestedStocks;
                    toStocks = invested;
                }

                deposit += toDeposit;
                if (useInstruments && toDeposit > 0m)
                {
                    string depositTarget = rule?.TargetInstrumentId is { Length: > 0 } tid
                        && instrumentBalances.ContainsKey(tid)
                        ? tid : primaryDepositId!;
                    string targetCurr = instrumentCurrency.GetValueOrDefault(depositTarget, baseCurrency);
                    decimal nativeAmount = FromBase(toDeposit, targetCurr, baseCurrency, fxRates);
                    instrumentBalances[depositTarget] += nativeAmount;
                    if (fundUnits.ContainsKey(depositTarget))
                    {
                        decimal navCur = fundNav.GetValueOrDefault(depositTarget, 100m);
                        if (navCur > 0m)
                            fundUnits[depositTarget] += nativeAmount / navCur;
                    }
                }
                totalToDeposit += toDeposit;
            }
            else if (input.Shortfall == ShortfallPolicy.Deposit)
            {
                deposit += surplus;
                if (useInstruments)
                {
                    string primaryCurr = instrumentCurrency.GetValueOrDefault(primaryDepositId!, baseCurrency);
                    instrumentBalances[primaryDepositId!] += FromBase(surplus, primaryCurr, baseCurrency, fxRates);
                }
            }

            decimal depositBeforeDivs = deposit;
            Dictionary<string, decimal> perStockDividends = ApplyDividends(input, shares, priceFactor, yearIndex, ref deposit);
            foreach ((string sym, decimal div) in perStockDividends)
                totalDividendsPerSymbol[sym] = totalDividendsPerSymbol.GetValueOrDefault(sym) + div;
            if (useInstruments)
            {
                decimal divCashAdded = deposit - depositBeforeDivs;
                if (divCashAdded != 0m)
                {
                    string primaryCurr = instrumentCurrency.GetValueOrDefault(primaryDepositId!, baseCurrency);
                    instrumentBalances[primaryDepositId!] += FromBase(divCashAdded, primaryCurr, baseCurrency, fxRates);
                }
            }

            Dictionary<string, decimal> perStockValue = PerStockValue(shares, input.StartingPrices, priceFactor);

            decimal stockValue = 0m;
            foreach (decimal value in perStockValue.Values)
                stockValue += value;

            decimal netWorth = deposit + stockValue;

            totalIncome += input.MonthlyIncome;
            totalExpenses += monthExpenses;

            List<EngineInstrumentSnapshot>? perInstrument = null;
            if (useInstruments)
            {
                perInstrument = [];
                foreach (EngineInstrument inst in input.Instruments!)
                {
                    bool isFund = string.Equals(inst.Type, "FUND", StringComparison.OrdinalIgnoreCase);
                    decimal bal = instrumentBalances.GetValueOrDefault(inst.Id);
                    perInstrument.Add(new EngineInstrumentSnapshot(
                        inst.Id, inst.Name, inst.Type, inst.Currency,
                        bal,
                        instrumentInterestTotal.GetValueOrDefault(inst.Id),
                        ToBase(bal, inst.Currency, baseCurrency, fxRates),
                        isFund ? fundUnits.GetValueOrDefault(inst.Id) : 0m,
                        isFund ? fundNav.GetValueOrDefault(inst.Id) : 0m));
                }
            }

            rows.Add(new EngineRow(
                month, input.MonthlyIncome, monthExpenses, toDeposit, toStocks, interest,
                deposit, stockValue, netWorth,
                perStockInvested, perStockDividends, perStockValue, new Dictionary<string, decimal>(shares),
                perInstrument));

            priceFactor *= 1m + monthlyStockRate;
        }

        EngineRow last = rows[^1];

        Dictionary<string, decimal> marketValueMap = new(StringComparer.OrdinalIgnoreCase);
        decimal totalPortfolioVal = 0m;
        foreach ((string symbol, decimal val) in last.PerStockValue)
        {
            totalPortfolioVal += val;
            string market = symbol.ToUpperInvariant() switch
            {
                var s when s.EndsWith(".RO") => "BVB (România)",
                var s when s.EndsWith(".L") => "UK (Marea Britanie)",
                var s when s.EndsWith(".AS") => "NL (Amsterdam)",
                var s when s.EndsWith(".DE") => "DE (Germania)",
                _ => "US (Statele Unite)"
            };
            marketValueMap[market] = marketValueMap.GetValueOrDefault(market) + val;
        }

        Dictionary<string, decimal> marketExposure = new(StringComparer.OrdinalIgnoreCase);
        if (totalPortfolioVal > 0m)
        {
            foreach ((string m, decimal v) in marketValueMap)
                marketExposure[m] = Math.Round(v / totalPortfolioVal * 100m, 1);
        }

        Dictionary<string, decimal> stockValueByMarket = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string m, decimal v) in marketValueMap)
            stockValueByMarket[m] = Math.Round(v, 2);

        Dictionary<string, decimal> currencyBreakdown = new(StringComparer.OrdinalIgnoreCase);
        if (useInstruments)
        {
            foreach (EngineInstrument inst in input.Instruments!)
                currencyBreakdown[inst.Currency] = currencyBreakdown.GetValueOrDefault(inst.Currency)
                    + instrumentBalances.GetValueOrDefault(inst.Id);
        }
        else
        {
            currencyBreakdown["RON"] = last.DepositBalance;
        }
        foreach ((string symbol, decimal val) in last.PerStockValue)
        {
            string curr = symbol.ToUpperInvariant() switch
            {
                var s when s.EndsWith(".RO") => "RON",
                var s when s.EndsWith(".L") => "GBP",
                var s when s.EndsWith(".AS") || s.EndsWith(".DE") => "EUR",
                _ => "USD"
            };
            currencyBreakdown[curr] = currencyBreakdown.GetValueOrDefault(curr) + val;
        }

        decimal totalStockCapitalGains = Math.Max(0m, last.StockValue - totalInvested);

        decimal summaryDepositInterest = totalInterest;
        decimal summaryBondCoupons = 0m;
        if (useInstruments)
        {
            summaryDepositInterest = 0m;
            foreach (EngineInstrument inst in input.Instruments!)
            {
                decimal earned = instrumentInterestTotal.GetValueOrDefault(inst.Id);
                if (string.Equals(inst.Type, "BOND", StringComparison.OrdinalIgnoreCase))
                    summaryBondCoupons += earned;
                else
                    summaryDepositInterest += earned;
            }
        }

        Dictionary<string, decimal> netWorthByCurrency = new(StringComparer.OrdinalIgnoreCase);
        netWorthByCurrency[baseCurrency] = last.NetWorth;
        foreach (string cur in new[] { "EUR", "USD", "GBP", "SEK", "DKK", "NOK" })
        {
            if (string.Equals(cur, baseCurrency, StringComparison.OrdinalIgnoreCase))
                continue;
            decimal rate = fxRates.GetValueOrDefault(cur);
            if (rate > 0m)
                netWorthByCurrency[cur] = last.NetWorth / rate;
        }

        EngineSummary summary = new(
            last.NetWorth, last.DepositBalance, last.StockValue,
            totalInvested, totalToDeposit, totalInterest, totalIncome, totalExpenses,
            totalStockCapitalGains, summaryDepositInterest, summaryBondCoupons,
            currencyBreakdown, marketExposure, netWorthByCurrency, stockValueByMarket);

        return new EngineResult(rows, summary, shares, investedPerSymbol, totalDividendsPerSymbol);
    }

    private static EngineAllocationRule? FirstActive(
        IReadOnlyList<EngineAllocationRule> rules, DateOnly month, decimal deposit, int distinctStocks,
        IReadOnlyDictionary<string, decimal> instrumentBalances)
    {
        foreach (EngineAllocationRule rule in rules)
        {
            decimal checkDeposit = deposit;
            if (rule.Condition.Type == ConditionType.UntilDeposit
                && rule.TargetInstrumentId is { Length: > 0 } targetId
                && instrumentBalances.Count > 0)
            {
                checkDeposit = instrumentBalances.GetValueOrDefault(targetId);
            }

            if (IsActive(rule.Condition, month, checkDeposit, distinctStocks, string.Empty, Empty, Empty))
                return rule;
        }

        return null;
    }

    private static bool IsActive(
        EngineCondition c,
        DateOnly month,
        decimal deposit,
        int distinctStocks,
        string symbol,
        IReadOnlyDictionary<string, decimal> shares,
        IReadOnlyDictionary<string, decimal> investedPerSymbol) => c.Type switch
        {
            ConditionType.Always => true,
            ConditionType.UntilDate => c.Date is { } d && month < d,
            ConditionType.UntilDeposit => c.Amount is { } a && deposit < a,
            ConditionType.UntilStockCount => c.Count is { } n && distinctStocks < n,
            ConditionType.UntilThisShares => c.Decimal is { } q && shares.GetValueOrDefault(symbol) < q,
            ConditionType.UntilThisInvested => c.Amount is { } m && investedPerSymbol.GetValueOrDefault(symbol) < m,
            ConditionType.BetweenDates => c.StartDate is { } sd && c.Date is { } ed && month >= sd && month <= ed,
            _ => false
        };

    private static (decimal ToDeposit, decimal ToStocks) Split(EngineAllocationRule rule, decimal surplus)
    {
        decimal toDeposit, toStocks;

        if (rule.Mode == AllocationMode.Percent)
        {
            toDeposit = Math.Max(0m, surplus * rule.DepositPct / 100m);
            toStocks = Math.Max(0m, surplus * rule.StocksPct / 100m);
        }
        else
        {
            toDeposit = Math.Clamp(rule.DepositAmount, 0m, surplus);
            toStocks = Math.Clamp(rule.StocksAmount, 0m, surplus - toDeposit);
        }

        toDeposit = Math.Min(toDeposit, surplus);
        toStocks = Math.Min(toStocks, surplus - toDeposit);

        return (toDeposit, toStocks);
    }

    private static decimal BuyStocks(
        EngineInput input,
        Dictionary<string, decimal> shares,
        Dictionary<string, decimal> investedPerSymbol,
        Dictionary<string, decimal> perStockInvested,
        decimal amount,
        DateOnly month,
        int distinctStocks,
        decimal deposit,
        decimal priceFactor)
    {
        if (amount <= 0m)
            return 0m;

        return input.StockGroups is { Count: > 0 }
            ? BuyFromGroups(input, shares, investedPerSymbol, perStockInvested, amount, month, distinctStocks, deposit, priceFactor)
            : BuyFromRules(input, shares, investedPerSymbol, perStockInvested, amount, month, distinctStocks, deposit, priceFactor);
    }

    private static decimal BuyFromGroups(
        EngineInput input,
        Dictionary<string, decimal> shares,
        Dictionary<string, decimal> investedPerSymbol,
        Dictionary<string, decimal> perStockInvested,
        decimal amount,
        DateOnly month,
        int distinctStocks,
        decimal deposit,
        decimal priceFactor)
    {
        IReadOnlyList<EngineStockGroup> groups = input.StockGroups!;

        if (groups.Count == 0 || amount <= 0m)
            return 0m;

        decimal totalWeight = 0m;
        foreach (EngineStockGroup group in groups)
            if (group.WeightPct > 0m)
                totalWeight += group.WeightPct;

        bool isParallelWeightedGroups = totalWeight >= 99m && totalWeight <= 101m;

        if (isParallelWeightedGroups)
        {
            decimal invested = 0m;
            foreach (EngineStockGroup group in groups)
            {
                if (group.WeightPct <= 0m || group.Members is not { Count: > 0 })
                    continue;

                decimal groupSlice = amount * group.WeightPct / 100m;
                if (groupSlice <= 0m)
                    continue;

                List<EngineStockMember> active = [];
                foreach (EngineStockMember m in group.Members)
                {
                    if (IsActive(m.Condition, month, deposit, distinctStocks, m.Symbol, shares, investedPerSymbol))
                    {
                        active.Add(m);
                    }
                }

                if (active.Count > 0)
                {
                    invested += BuyInsideGroup(input, shares, investedPerSymbol, perStockInvested, group, active, groupSlice, month, distinctStocks, deposit, priceFactor);
                }
            }

            return invested;
        }

        // Sequential Pipeline Stages (0% weights on stages):
        decimal totalInvestedSequential = 0m;
        decimal remainingAmount = amount;

        foreach (EngineStockGroup group in groups)
        {
            if (remainingAmount <= 0m)
                break;

            if (group.Members is not { Count: > 0 })
                continue;

            List<EngineStockMember> activeMembers = [];
            foreach (EngineStockMember m in group.Members)
            {
                if (IsActive(m.Condition, month, deposit, distinctStocks, m.Symbol, shares, investedPerSymbol))
                {
                    activeMembers.Add(m);
                }
            }

            if (activeMembers.Count > 0)
            {
                decimal spent = BuyInsideGroup(
                    input, shares, investedPerSymbol, perStockInvested,
                    group, activeMembers, remainingAmount, month, distinctStocks, deposit, priceFactor);

                totalInvestedSequential += spent;
                remainingAmount -= spent;

                bool stageStillActive = false;
                foreach (EngineStockMember m in group.Members)
                {
                    if (IsActive(m.Condition, month, deposit, distinctStocks, m.Symbol, shares, investedPerSymbol))
                    {
                        stageStillActive = true;
                        break;
                    }
                }
                if (stageStillActive)
                    break;
            }
        }

        return totalInvestedSequential;
    }

    private static decimal BuyInsideGroup(
        EngineInput input,
        Dictionary<string, decimal> shares,
        Dictionary<string, decimal> investedPerSymbol,
        Dictionary<string, decimal> perStockInvested,
        EngineStockGroup group,
        List<EngineStockMember> activeMembers,
        decimal groupBudget,
        DateOnly month,
        int distinctStocks,
        decimal deposit,
        decimal priceFactor)
    {
        if (groupBudget <= 0m || activeMembers.Count == 0)
            return 0m;

        if (group.Mode == GroupAllocationMode.Sequential)
        {
            decimal totalBought = 0m;
            decimal remaining = groupBudget;

            foreach (EngineStockMember m in activeMembers)
            {
                if (remaining <= 0m) break;
                decimal spent = Buy(input, shares, investedPerSymbol, perStockInvested, m.Symbol, remaining, priceFactor, m.Condition);
                totalBought += spent;
                remaining -= spent;
            }

            return totalBought;
        }

        decimal totalInvested = 0m;
        decimal budget = groupBudget;
        List<EngineStockMember> current = new(activeMembers);

        while (budget > 0.01m && current.Count > 0)
        {
            bool hasCustomWeights = current.Any(m => m.WeightPct > 0m);
            decimal weightSum = hasCustomWeights
                ? current.Sum(m => m.WeightPct > 0m ? m.WeightPct : 1m)
                : current.Count;

            if (weightSum <= 0m) break;

            decimal roundSpent = 0m;
            List<EngineStockMember> next = [];

            foreach (EngineStockMember m in current)
            {
                decimal weight = hasCustomWeights ? (m.WeightPct > 0m ? m.WeightPct : 1m) : 1m;
                decimal slice = budget * weight / weightSum;
                decimal spent = Buy(input, shares, investedPerSymbol, perStockInvested, m.Symbol, slice, priceFactor, m.Condition);
                roundSpent += spent;
                if (spent >= slice - 0.01m)
                    next.Add(m);
            }

            totalInvested += roundSpent;
            budget -= roundSpent;
            if (roundSpent < 0.01m) break;
            current = next;
        }

        return totalInvested;
    }

    private static decimal BuyFromRules(
        EngineInput input,
        Dictionary<string, decimal> shares,
        Dictionary<string, decimal> investedPerSymbol,
        Dictionary<string, decimal> perStockInvested,
        decimal amount,
        DateOnly month,
        int distinctStocks,
        decimal deposit,
        decimal priceFactor)
    {
        List<EngineStockRule> active = [];
        foreach (EngineStockRule rule in input.StockRules)
            if (IsActive(rule.Condition, month, deposit, distinctStocks, rule.Symbol, shares, investedPerSymbol))
                active.Add(rule);

        if (active.Count == 0)
            return 0m;

        bool allWeighted = active.TrueForAll(r => r.WeightPct is > 0m);
        decimal totalWeight = allWeighted ? active.Sum(r => r.WeightPct!.Value) : active.Count;

        if (totalWeight <= 0m)
            return 0m;

        decimal invested = 0m;

        foreach (EngineStockRule rule in active)
        {
            decimal weight = allWeighted ? rule.WeightPct!.Value : 1m;
            decimal slice = amount * weight / totalWeight;

            invested += Buy(input, shares, investedPerSymbol, perStockInvested, rule.Symbol, slice, priceFactor, rule.Condition);
        }

        return invested;
    }

    private static decimal Buy(
        EngineInput input,
        Dictionary<string, decimal> shares,
        Dictionary<string, decimal> investedPerSymbol,
        Dictionary<string, decimal> perStockInvested,
        string symbol,
        decimal amount,
        decimal priceFactor,
        EngineCondition? condition = null)
    {
        if (amount <= 0m || string.IsNullOrWhiteSpace(symbol))
            return 0m;

        if (!input.StartingPrices.TryGetValue(symbol, out decimal basePrice) || basePrice <= 0m)
        {
            basePrice = symbol.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? 25.00m : 100.00m;
        }

        decimal price = basePrice * priceFactor;

        if (price <= 0m)
            return 0m;

        decimal sharesToBuy = Math.Floor(amount / price);

        if (condition is not null)
        {
            if (condition.Type == ConditionType.UntilThisShares && condition.Decimal is { } targetShares)
            {
                decimal currentShares = shares.GetValueOrDefault(symbol);
                decimal remainingSharesNeeded = Math.Max(0m, targetShares - currentShares);
                sharesToBuy = Math.Min(sharesToBuy, remainingSharesNeeded);
            }
            else if (condition.Type == ConditionType.UntilThisInvested && condition.Amount is { } targetInvested)
            {
                decimal currentInvested = investedPerSymbol.GetValueOrDefault(symbol);
                decimal remainingInvestedNeeded = Math.Max(0m, targetInvested - currentInvested);
                decimal maxSharesForInvested = Math.Floor(remainingInvestedNeeded / price);
                sharesToBuy = Math.Min(sharesToBuy, maxSharesForInvested);
            }
        }

        if (sharesToBuy <= 0m)
            return 0m;

        decimal actualSpent = sharesToBuy * price;

        shares[symbol] = shares.GetValueOrDefault(symbol) + sharesToBuy;
        investedPerSymbol[symbol] = investedPerSymbol.GetValueOrDefault(symbol) + actualSpent;
        perStockInvested[symbol] = perStockInvested.GetValueOrDefault(symbol) + actualSpent;

        return actualSpent;
    }

    private static Dictionary<string, decimal> ApplyDividends(
        EngineInput input,
        Dictionary<string, decimal> shares,
        decimal priceFactor,
        int yearIndex,
        ref decimal deposit)
    {
        Dictionary<string, decimal> received = new(StringComparer.OrdinalIgnoreCase);

        if (input.DividendPerShare is not { Count: > 0 })
            return received;

        decimal netFactor = 1m - input.DividendTaxRate;

        if (netFactor <= 0m)
            return received;

        List<string> symbols = [.. shares.Keys];

        foreach (string symbol in symbols)
        {
            decimal qty = shares[symbol];

            if (qty <= 0m)
                continue;

            if (!input.DividendPerShare.TryGetValue(symbol, out decimal annualDps) || annualDps <= 0m)
                continue;

            decimal growth = input.DividendGrowthRate?.GetValueOrDefault(symbol) ?? 0m;
            decimal monthlyDps = annualDps * Pow(1m + growth, yearIndex) / 12m;

            if (monthlyDps <= 0m)
                continue;

            decimal net = qty * monthlyDps * netFactor;

            if (net <= 0m)
                continue;

            received[symbol] = received.GetValueOrDefault(symbol) + net;

            bool priced = input.StartingPrices.TryGetValue(symbol, out decimal basePrice) && basePrice > 0m;
            if (!priced)
            {
                basePrice = symbol.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? 25.00m : 100.00m;
                priced = true;
            }

            if (input.ReinvestDividends && priced)
                shares[symbol] = qty + net / (basePrice * priceFactor);
            else
                deposit += net;
        }

        return received;
    }

    private static Dictionary<string, decimal> PerStockValue(
        IReadOnlyDictionary<string, decimal> shares,
        IReadOnlyDictionary<string, decimal> startingPrices,
        decimal priceFactor)
    {
        Dictionary<string, decimal> values = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string symbol, decimal qty) in shares)
        {
            if (qty <= 0m) continue;
            if (!startingPrices.TryGetValue(symbol, out decimal basePrice) || basePrice <= 0m)
            {
                basePrice = symbol.EndsWith(".RO", StringComparison.OrdinalIgnoreCase) ? 25.00m : 100.00m;
            }
            values[symbol] = qty * basePrice * priceFactor;
        }

        return values;
    }

    private static decimal Pow(decimal value, int exponent)
    {
        decimal result = 1m;

        for (int i = 0; i < exponent; i++)
            result *= value;

        return result;
    }

    private static int CountDistinctStocks(IReadOnlyDictionary<string, decimal> shares)
    {
        int count = 0;

        foreach (decimal qty in shares.Values)
            if (qty > 0m)
                count++;

        return count;
    }

    private static decimal ToBase(decimal amount, string currency, string baseCurrency, IReadOnlyDictionary<string, decimal> fxRates)
    {
        if (string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase) || amount == 0m)
            return amount;
        decimal rate = fxRates.GetValueOrDefault(currency);
        return rate > 0m ? amount * rate : amount;
    }

    private static decimal FromBase(decimal baseAmount, string targetCurrency, string baseCurrency, IReadOnlyDictionary<string, decimal> fxRates)
    {
        if (string.Equals(targetCurrency, baseCurrency, StringComparison.OrdinalIgnoreCase) || baseAmount == 0m)
            return baseAmount;
        decimal rate = fxRates.GetValueOrDefault(targetCurrency);
        return rate > 0m ? baseAmount / rate : baseAmount;
    }
}

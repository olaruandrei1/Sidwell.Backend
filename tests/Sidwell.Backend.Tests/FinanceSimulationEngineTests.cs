using Sidwell.Backend.Application.Implementations;

namespace Sidwell.Backend.Tests;

public sealed class FinanceSimulationEngineTests
{
    private static EngineCondition Always() => new(ConditionType.Always);
    private static EngineCondition UntilDeposit(decimal amount) => new(ConditionType.UntilDeposit, Amount: amount);
    private static EngineCondition UntilStockCount(int count) => new(ConditionType.UntilStockCount, Count: count);
    private static EngineCondition UntilDate(DateOnly date) => new(ConditionType.UntilDate, Date: date);
    private static EngineCondition UntilThisShares(decimal shares) => new(ConditionType.UntilThisShares, Decimal: shares);
    private static EngineCondition UntilThisInvested(decimal amount) => new(ConditionType.UntilThisInvested, Amount: amount);

    private static EngineAllocationRule Percent(EngineCondition condition, decimal depositPct, decimal stocksPct) =>
        new(condition, AllocationMode.Percent, depositPct, stocksPct, 0m, 0m);

    private static EngineAllocationRule Amount(EngineCondition condition, decimal depositAmount, decimal stocksAmount) =>
        new(condition, AllocationMode.Amount, 0m, 0m, depositAmount, stocksAmount);

    private static EngineInput BaseInput(
        IReadOnlyList<EngineAllocationRule> allocationRules,
        decimal monthlyIncome = 1000m,
        decimal fixedExpense = 0m,
        decimal startingDeposit = 0m,
        decimal depositRatePct = 0m,
        decimal stockGrowthPct = 0m,
        int horizonYear = 2025,
        IReadOnlyList<EngineStockRule>? stockRules = null,
        IReadOnlyList<EnginePlannedExpense>? plannedExpenses = null,
        IReadOnlyDictionary<string, decimal>? startingPrices = null,
        IReadOnlyDictionary<string, decimal>? startingShares = null,
        ShortfallPolicy shortfall = ShortfallPolicy.None,
        IReadOnlyList<EngineStockGroup>? stockGroups = null,
        IReadOnlyDictionary<string, decimal>? startingInvested = null,
        IReadOnlyDictionary<string, decimal>? dividendPerShare = null,
        IReadOnlyDictionary<string, decimal>? dividendGrowthRate = null,
        decimal dividendTaxRate = 0m,
        bool reinvestDividends = false) =>
        new(
            Start: new DateOnly(2025, 1, 1),
            HorizonYear: horizonYear,
            MonthlyIncome: monthlyIncome,
            FixedMonthlyExpense: fixedExpense,
            StartingDeposit: startingDeposit,
            DepositAnnualRatePct: depositRatePct,
            StockAnnualGrowthPct: stockGrowthPct,
            AllocationRules: allocationRules,
            StockRules: stockRules ?? [],
            PlannedExpenses: plannedExpenses ?? [],
            StartingPrices: startingPrices ?? new Dictionary<string, decimal>(),
            StartingShares: startingShares ?? new Dictionary<string, decimal>(),
            Shortfall: shortfall,
            StockGroups: stockGroups,
            StartingInvested: startingInvested,
            DividendPerShare: dividendPerShare,
            DividendGrowthRate: dividendGrowthRate,
            DividendTaxRate: dividendTaxRate,
            ReinvestDividends: reinvestDividends);

    [Fact]
    public void DepositCompounding_AppliesInterestToOpeningBalanceThenContribution()
    {
        // 12% annual => 1% monthly. Interest hits the opening balance before the month's contribution.
        EngineInput input = BaseInput([Percent(Always(), 100m, 0m)], depositRatePct: 12m);

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[0].DepositBalance);              // 0 + 0 interest + 1000
        Assert.Equal(2010m, result.Rows[1].DepositBalance);              // 1000*1.01 + 1000
        Assert.Equal(3030.10m, result.Rows[2].DepositBalance);           // 2010*1.01 + 1000
        Assert.True(result.Summary.TotalInterest > 0m);
        Assert.Equal(result.Rows[^1].DepositBalance, result.Summary.FinalDeposit);
    }

    [Fact]
    public void PercentSplit_DividesSurplusBetweenDepositAndStocks()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 60m, 40m)],
            stockRules: [new EngineStockRule("VOO", null, Always())],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(600m, result.Rows[0].ToDeposit);
        Assert.Equal(400m, result.Rows[0].ToStocks);
        Assert.Equal(600m, result.Rows[0].DepositBalance);
        Assert.Equal(400m, result.Rows[0].StockValue);
        Assert.Equal(1000m, result.Rows[0].NetWorth);
        Assert.Equal(48m, result.FinalShares["VOO"]);  // 4 shares/month * 12 months (price constant, growth 0)
    }

    [Fact]
    public void AmountSplit_CapsAtSurplusWithDepositPriority()
    {
        // deposit 700 + stocks 500 requested against a 1000 surplus => stocks capped to the remaining 300.
        EngineInput input = BaseInput(
            [Amount(Always(), 700m, 500m)],
            horizonYear: 2025,
            stockRules: [new EngineStockRule("VOO", null, Always())],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(700m, result.Rows[0].ToDeposit);
        Assert.Equal(300m, result.Rows[0].ToStocks);
    }

    [Fact]
    public void RulePrecedence_FirstMatchingConditionWins_AndFlipsWhenThresholdCrossed()
    {
        // Until the deposit reaches 2000 everything goes to deposit; after that, everything goes to stocks.
        EngineInput input = BaseInput(
            [
                Percent(UntilDeposit(2000m), 100m, 0m),
                Percent(Always(), 0m, 100m),
            ],
            stockRules: [new EngineStockRule("VOO", null, Always())],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        // Months 1-2 fill the deposit.
        Assert.Equal(1000m, result.Rows[0].DepositBalance);
        Assert.Equal(2000m, result.Rows[1].DepositBalance);
        Assert.Equal(0m, result.Rows[1].ToStocks);

        // Month 3 flips: deposit no longer < 2000, so the surplus buys stocks.
        Assert.Equal(0m, result.Rows[2].ToDeposit);
        Assert.Equal(1000m, result.Rows[2].ToStocks);
        Assert.Equal(2000m, result.Rows[2].DepositBalance);
        Assert.Equal(1000m, result.Rows[2].StockValue);
    }

    [Fact]
    public void StockSelection_UntilStockCount_StopsBuyingASymbolOnceThresholdReached()
    {
        // VOO always; AAPL only while we own fewer than 2 distinct stocks.
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockRules:
            [
                new EngineStockRule("VOO", null, Always()),
                new EngineStockRule("AAPL", null, UntilStockCount(2)),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m, ["AAPL"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        // Month 1: 0 distinct stocks => both active, equal split 500/500 => 5 shares each.
        // Month 2+: 2 distinct stocks => AAPL condition false => only VOO bought.
        Assert.Equal(5m, result.FinalShares["AAPL"]);           // frozen after month 1
        Assert.True(result.FinalShares["VOO"] > 5m);            // keeps accumulating
    }

    [Fact]
    public void MultiStock_ExplicitWeights_SplitTheStockMoneyProportionally()
    {
        // 70/30 weights, single month, equal prices => share counts reflect the weights.
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            horizonYear: 2025,
            stockRules:
            [
                new EngineStockRule("VOO", 70m, Always()),
                new EngineStockRule("AAPL", 30m, Always()),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m, ["AAPL"] = 100m });

        // Only look at the first month's allocation by using a one-month horizon-equivalent check:
        EngineResult result = FinanceSimulationEngine.Run(input);

        // Ratio of shares must equal the weight ratio regardless of how many months ran.
        decimal ratio = result.FinalShares["VOO"] / result.FinalShares["AAPL"];
        Assert.Equal(70m / 30m, ratio, precision: 6);
    }

    [Fact]
    public void PlannedExpense_Shortfall_DepositPolicy_DrawsFromDeposit()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 100m, 0m)],
            startingDeposit: 3000m,
            plannedExpenses: [new EnginePlannedExpense(new DateOnly(2025, 2, 1), 5000m)],
            shortfall: ShortfallPolicy.Deposit);

        EngineResult result = FinanceSimulationEngine.Run(input);

        // Month 1: 3000 + 1000 = 4000. Month 2: surplus = 1000 - 5000 = -4000 => 4000 - 4000 = 0.
        Assert.Equal(4000m, result.Rows[0].DepositBalance);
        Assert.Equal(0m, result.Rows[1].DepositBalance);
        Assert.Equal(0m, result.Rows[1].ToDeposit);
    }

    [Fact]
    public void PlannedExpense_Shortfall_NonePolicy_LeavesDepositUntouched()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 100m, 0m)],
            startingDeposit: 3000m,
            plannedExpenses: [new EnginePlannedExpense(new DateOnly(2025, 2, 1), 5000m)],
            shortfall: ShortfallPolicy.None);

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(4000m, result.Rows[0].DepositBalance);
        Assert.Equal(4000m, result.Rows[1].DepositBalance);  // shortfall ignored, nothing invested
        Assert.Equal(0m, result.Rows[1].ToDeposit);
    }

    [Fact]
    public void UntilDate_Condition_ActiveOnlyBeforeTheGivenMonth()
    {
        EngineInput input = BaseInput(
            [
                Percent(UntilDate(new DateOnly(2025, 3, 1)), 100m, 0m),
                Percent(Always(), 0m, 100m),
            ],
            stockRules: [new EngineStockRule("VOO", null, Always())],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[0].ToDeposit);   // Jan < Mar => deposit
        Assert.Equal(1000m, result.Rows[1].ToDeposit);   // Feb < Mar => deposit
        Assert.Equal(0m, result.Rows[2].ToDeposit);      // Mar not < Mar => flips to stocks
        Assert.Equal(1000m, result.Rows[2].ToStocks);
    }

    [Fact]
    public void NoActiveStockRule_RollsStockMoneyIntoDepositSoNothingIsLost()
    {
        // Allocation sends 100% to stocks, but no stock rule matches => money must not vanish.
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockRules: [new EngineStockRule("VOO", null, UntilStockCount(0))]); // never active (0 < 0 is false)

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[0].DepositBalance); // rolled into deposit
        Assert.Equal(0m, result.Rows[0].StockValue);
    }

    [Fact]
    public void SequentialGroup_FillsTheFirstMemberThenAdvancesToTheNext()
    {
        // 1000/month into a single sequential group. AAPL at 400 => 2.5 shares/month, so it takes two
        // months to reach the 5-share threshold; from month 3 the whole slice goes to MSFT.
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Sequential,
                [
                    new EngineStockMember("AAPL", UntilThisShares(5m)),
                    new EngineStockMember("MSFT", Always()),
                ]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["AAPL"] = 400m, ["MSFT"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(800m, result.Rows[0].PerStockInvested["AAPL"]);
        Assert.Equal(800m, result.Rows[1].PerStockInvested["AAPL"]);
        Assert.Equal(200m, result.Rows[0].PerStockInvested["MSFT"]);
        Assert.Equal(200m, result.Rows[1].PerStockInvested["MSFT"]);

        Assert.Equal(5m, result.FinalShares["AAPL"]);              // frozen once the threshold is reached (5 shares)
        Assert.Equal(2000m, result.FinalInvestedPerSymbol["AAPL"]);
        Assert.Equal(10000m, result.FinalInvestedPerSymbol["MSFT"]);
    }

    [Fact]
    public void SequentialGroup_TransfersTheFullSliceToTheNextMemberNotAShare()
    {
        // Once AAPL is done MSFT must receive 100% of the group's slice, not a 50/50 split.
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Sequential,
                [
                    new EngineStockMember("AAPL", UntilThisShares(5m)),
                    new EngineStockMember("MSFT", Always()),
                ]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["AAPL"] = 400m, ["MSFT"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[3].PerStockInvested["MSFT"]);
        Assert.False(result.Rows[3].PerStockInvested.ContainsKey("AAPL"));
        Assert.Equal(1000m, result.Rows[3].ToStocks);
    }

    [Fact]
    public void SequentialGroup_UntilThisInvested_AdvancesOnAccumulatedContribution()
    {
        // AAPL takes the slice until 2500 has been contributed to it (month 3 crosses the line).
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Sequential,
                [
                    new EngineStockMember("AAPL", UntilThisInvested(2500m)),
                    new EngineStockMember("MSFT", Always()),
                ]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["AAPL"] = 100m, ["MSFT"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(2500m, result.FinalInvestedPerSymbol["AAPL"]);   // capped at 2500, then remaining 500 goes to MSFT
        Assert.Equal(1000m, result.Rows[3].PerStockInvested["MSFT"]);
    }

    [Fact]
    public void WeightedGroup_SplitsTheSliceEquallyBetweenActiveMembers()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Weighted,
                [
                    new EngineStockMember("VOO", Always()),
                    new EngineStockMember("QQQ", Always()),
                ]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m, ["QQQ"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(500m, result.Rows[0].PerStockInvested["VOO"]);
        Assert.Equal(500m, result.Rows[0].PerStockInvested["QQQ"]);
        Assert.Equal(6000m, result.FinalInvestedPerSymbol["VOO"]);
        Assert.Equal(6000m, result.FinalInvestedPerSymbol["QQQ"]);
    }

    [Fact]
    public void GroupWeights_SplitTheStockMoneyAcrossGroups()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(60m, GroupAllocationMode.Weighted,
                    [new EngineStockMember("VOO", Always())]),
                new EngineStockGroup(40m, GroupAllocationMode.Sequential,
                    [new EngineStockMember("AAPL", Always())]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m, ["AAPL"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(600m, result.Rows[0].PerStockInvested["VOO"]);
        Assert.Equal(400m, result.Rows[0].PerStockInvested["AAPL"]);
        Assert.Equal(7200m, result.FinalInvestedPerSymbol["VOO"]);
        Assert.Equal(4800m, result.FinalInvestedPerSymbol["AAPL"]);
    }

    [Fact]
    public void Dividends_Reinvested_CompoundShareCountMonthOverMonth()
    {
        // 12/share/year => 1/share/month. 10 shares, 16% tax => 8.40 net in month 1, reinvested at 100.
        EngineInput input = BaseInput(
            [Percent(Always(), 100m, 0m)],
            startingPrices: new Dictionary<string, decimal> { ["SYM"] = 100m },
            startingShares: new Dictionary<string, decimal> { ["SYM"] = 10m },
            dividendPerShare: new Dictionary<string, decimal> { ["SYM"] = 12m },
            dividendGrowthRate: new Dictionary<string, decimal> { ["SYM"] = 0.06m },
            dividendTaxRate: 0.16m,
            reinvestDividends: true);

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(8.40m, result.Rows[0].PerStockDividends["SYM"]);
        Assert.True(result.Rows[0].PerStockDividends["SYM"] > 0m);
        Assert.True(result.Rows[1].PerStockDividends["SYM"] > result.Rows[0].PerStockDividends["SYM"]);
        Assert.Equal(1008.40m, result.Rows[0].PerStockValue["SYM"]);    // 10.084 shares at a flat 100
        Assert.True(result.Rows[1].PerStockValue["SYM"] > result.Rows[0].PerStockValue["SYM"]);
        Assert.True(result.FinalShares["SYM"] > 10.084m);
    }

    [Fact]
    public void Dividends_NotReinvested_LandInTheDeposit()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 0m)],
            monthlyIncome: 0m,
            startingPrices: new Dictionary<string, decimal> { ["SYM"] = 100m },
            startingShares: new Dictionary<string, decimal> { ["SYM"] = 10m },
            dividendPerShare: new Dictionary<string, decimal> { ["SYM"] = 12m },
            dividendTaxRate: 0.16m,
            reinvestDividends: false);

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(8.40m, result.Rows[0].DepositBalance);
        Assert.Equal(16.80m, result.Rows[1].DepositBalance);
        Assert.Equal(10m, result.FinalShares["SYM"]);   // share count untouched
    }

    [Fact]
    public void StockGroups_TakePrecedenceOverFlatStockRules()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockRules: [new EngineStockRule("IGNORED", null, Always())],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Weighted,
                    [new EngineStockMember("VOO", Always())]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m, ["IGNORED"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[0].PerStockInvested["VOO"]);
        Assert.False(result.FinalShares.ContainsKey("IGNORED"));
    }

    [Fact]
    public void NoActiveGroupMember_RollsTheSliceIntoTheDeposit()
    {
        EngineInput input = BaseInput(
            [Percent(Always(), 0m, 100m)],
            stockGroups:
            [
                new EngineStockGroup(100m, GroupAllocationMode.Sequential,
                    [new EngineStockMember("VOO", UntilThisShares(0m))]),
            ],
            startingPrices: new Dictionary<string, decimal> { ["VOO"] = 100m });

        EngineResult result = FinanceSimulationEngine.Run(input);

        Assert.Equal(1000m, result.Rows[0].DepositBalance);
        Assert.Equal(0m, result.Rows[0].StockValue);
    }
}

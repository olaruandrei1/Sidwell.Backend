using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

internal sealed record ReportTable(string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

internal static class ReportSectionData
{
    public static IReadOnlyList<(string Label, string Value)> BuildHeaderFacts(TickerDetail? detail)
    {
        if (detail is null) return [];
        List<(string, string)> facts =
        [
            ("Symbol", detail.Ticker.Symbol),
            ("Name", detail.Ticker.Name),
            ("Exchange", detail.Ticker.Exchange),
            ("Currency", detail.Ticker.Currency),
        ];
        if (detail.Price.Latest is not null)
        {
            facts.Add(("Latest close", $"{detail.Price.Latest.Close} ({detail.Price.Latest.Date})"));
            facts.Add(("Latest volume", detail.Price.Latest.Volume.ToString("N0")));
        }
        if (detail.Watchlisted) facts.Add(("Watchlisted", "Yes"));
        return facts;
    }

    public static IReadOnlyList<(string Label, string Value)> BuildCompositeFacts(CompositeScore? composite)
    {
        if (composite is null) return [];
        return
        [
            ("Philosophy", composite.Philosophy),
            ("Score", composite.Score),
            ("Label", composite.Label),
            ("Overridden by user", composite.Overridden ? "Yes" : "No"),
        ];
    }

    public static IReadOnlyList<(string Label, string Value)> BuildKeyStatsFacts(KeyStatsDto? k)
    {
        if (k is null) return [];
        return
        [
            ("52-week low", k.FiftyTwoWeekLow ?? "—"),
            ("52-week high", k.FiftyTwoWeekHigh ?? "—"),
            ("Beta", k.Beta ?? "—"),
            ("P/E (trailing)", k.PeTrailing ?? "—"),
            ("Market cap", k.MarketCap ?? "—"),
            ("Earnings date", k.EarningsDate ?? "—"),
            ("1-year target", k.TargetOneYear ?? "—"),
            ("Price / Book", k.PriceToBook ?? "—"),
            ("ROE (TTM)", k.RoeTtm ?? "—"),
            ("Debt / Equity", k.DebtToEquity ?? "—"),
            ("Revenue growth (YoY, TTM)", k.RevenueGrowthTtmYoy ?? "—"),
            ("EV / EBITDA", k.EvToEbitda ?? "—"),
            ("Analyst buy", k.AnalystBuy?.ToString() ?? "—"),
            ("Analyst hold", k.AnalystHold?.ToString() ?? "—"),
            ("Analyst sell", k.AnalystSell?.ToString() ?? "—"),
            ("Analyst consensus", k.AnalystConsensus ?? "—"),
        ];
    }

    public static IReadOnlyList<(string Label, string Value)> BuildDividendFacts(DividendInfoDto? d)
    {
        if (d is null) return [];
        return
        [
            ("Dividend yield", d.DividendYield ?? "—"),
            ("Forward dividend", d.ForwardDividend ?? "—"),
            ("Ex-dividend date", d.ExDividendDate ?? "—"),
            ("Pay frequency", d.PayFrequency ?? "—"),
            ("Historical growth (CAGR)", d.HistoricalGrowthCagr ?? "—"),
            ("Status", d.Status),
        ];
    }

    public static IReadOnlyList<(string Label, string Value)> BuildHoldingFacts(HoldingDto? h)
    {
        if (h is null) return [];
        List<(string, string)> facts =
        [
            ("Shares", h.Shares),
            ("Avg cost", h.AvgCost),
            ("Market value", h.MarketValue),
            ("Unrealized P&L", h.UnrealizedPnl),
            ("Realized P&L", h.RealizedPnl),
            ("Broker", h.Broker),
        ];
        if (!string.IsNullOrEmpty(h.TargetShares))
            facts.Add(("Target shares", $"{h.TargetShares}{(h.TargetReached ? " (reached)" : "")}"));
        return facts;
    }

    public static ReportTable BuildAlgorithmsTable(IReadOnlyList<AlgoScore> algos)
    {
        string[] headers = ["Algorithm", "Score", "Status", "Key output"];
        List<IReadOnlyList<string>> rows = [];
        foreach (AlgoScore a in algos.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            string keyOutput = "—";
            if (a.Details is not null)
            {
                foreach (string key in new[] { "interpretation", "zone", "flag", "margin_of_safety", "note" })
                {
                    if (a.Details.TryGetValue(key, out object? val) && val is not null)
                    {
                        string text = val.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text)) { keyOutput = text; break; }
                    }
                }
            }
            rows.Add([a.Name, a.Score ?? "N/A", a.Applicable ? "Applicable" : "Not applicable", keyOutput]);
        }
        return new ReportTable("Quantitative algorithms", headers, rows);
    }

    public static ReportTable BuildGatedAlgosTable(IReadOnlyList<GatedAlgo> gated)
    {
        string[] headers = ["Algorithm", "Missing data"];
        List<IReadOnlyList<string>> rows = gated.Select(g => (IReadOnlyList<string>)[g.AlgoName, g.MissingData]).ToList();
        return new ReportTable("Algorithms without enough data", headers, rows);
    }

    public static ReportTable BuildFundamentalsTable(IReadOnlyList<FundamentalPeriod> periods)
    {
        string[] headers = ["As of", "Period", "Revenue", "Net income", "Gross profit", "EBIT",
                             "Total assets", "Total liabilities", "Total equity", "EPS", "Shares outstanding"];
        List<IReadOnlyList<string>> rows = periods
            .OrderByDescending(p => p.AsOfDate, StringComparer.Ordinal)
            .Take(12)
            .Select(p => (IReadOnlyList<string>)
            [
                p.AsOfDate, p.Period, p.Revenue ?? "—", p.NetIncome ?? "—", p.GrossProfit ?? "—", p.Ebit ?? "—",
                p.TotalAssets ?? "—", p.TotalLiabilities ?? "—", p.TotalEquity ?? "—", p.Eps ?? "—",
                p.SharesOutstanding?.ToString("N0") ?? "—"
            ])
            .ToList();
        return new ReportTable("Fundamentals (most recent periods)", headers, rows);
    }

    public static ReportTable BuildPriceHistoryTable(IReadOnlyList<PriceBar> history)
    {
        string[] headers = ["Date", "Open", "High", "Low", "Close", "Volume"];
        List<IReadOnlyList<string>> rows = history
            .OrderByDescending(b => b.Date, StringComparer.Ordinal)
            .Take(60)
            .Select(b => (IReadOnlyList<string>)
            [
                b.Date, b.Open, b.High, b.Low, b.Close, b.Volume.ToString("N0")
            ])
            .ToList();
        return new ReportTable("Recent price history (last 60 sessions)", headers, rows);
    }

    public static ReportTable BuildNewsTable(IReadOnlyList<NewsItem> news)
    {
        string[] headers = ["Published", "Source", "Sentiment", "Title", "Url"];
        List<IReadOnlyList<string>> rows = news
            .Take(20)
            .Select(n => (IReadOnlyList<string>)
            [
                n.PublishedAt, n.Source, n.Sentiment ?? "—", n.Title, n.Url
            ])
            .ToList();
        return new ReportTable("Latest news", headers, rows);
    }
}

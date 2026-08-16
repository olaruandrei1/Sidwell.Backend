using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

public sealed class XlsxJournalReportRenderer : IJournalReportRenderer
{
    private static readonly XLColor BrandAccent = XLColor.FromHtml("#059669");
    private static readonly XLColor AccentSoft = XLColor.FromHtml("#ECFDF5");
    private static readonly XLColor CardBg = XLColor.FromHtml("#F9FAFB");
    private static readonly XLColor CardBgAlt = XLColor.FromHtml("#F3F4F6");
    private static readonly XLColor BrandNavy = XLColor.FromHtml("#111827");
    private static readonly XLColor BodyText = XLColor.FromHtml("#1F2937");
    private static readonly XLColor MutedGray = XLColor.FromHtml("#6B7280");
    private static readonly XLColor LightGray = XLColor.FromHtml("#9CA3AF");
    private static readonly XLColor BorderGray = XLColor.FromHtml("#E5E7EB");
    private static readonly XLColor SuccessColor = XLColor.FromHtml("#10B981");
    private static readonly XLColor WarningColor = XLColor.FromHtml("#F59E0B");
    private static readonly XLColor DangerColor = XLColor.FromHtml("#EF4444");

    private const int GridWidth = 12; // 12-column layout, like a Bootstrap grid

    public bool CanRender(ReportFormat format) => format == ReportFormat.Xlsx;

    public Task<JournalReportFile> RenderAsync(JournalReportContext context, ReportFormat format, CancellationToken ct = default)
    {
        if (!CanRender(format))
            throw new NotSupportedException($"{nameof(XlsxJournalReportRenderer)} only supports {nameof(ReportFormat.Xlsx)}.");

        using XLWorkbook workbook = new();
        IXLWorksheet ws = workbook.Worksheets.Add("Report");

        for (int c = 1; c <= GridWidth; c++) ws.Column(c).Width = 14;
        ws.ShowGridLines = false;

        int row = 2;
        row = WriteCoverBlock(ws, row, context);
        row = WriteNoteBlock(ws, row, context);

        if (context.TickerAnalysis is not null)
        {
            TickerDetail d = context.TickerAnalysis;
            if (d.Composite is not null) row = WriteVerdictHero(ws, row, d.Composite);
            row = WriteKeyStatsGrid(ws, row, d);
            row = WriteAlgorithmsGrid(ws, row, d.Algorithms);
            if (d.GatedAlgos.Count > 0) row = WriteGatedList(ws, row, d.GatedAlgos);
            if (d.Dividends is not null) row = WriteDividendsBlock(ws, row, d.Dividends);
            if (d.Holding is not null) row = WriteHoldingBlock(ws, row, d.Holding);
            if (d.Fundamentals.Count > 0) row = WriteFundamentalsBlock(ws, row, d.Fundamentals);
            if (d.Price.History.Count > 0) row = WritePriceHistoryBlock(ws, row, d.Price.History);
            if (d.News.Count > 0) row = WriteNewsBlock(ws, row, d.News);
        }

        if (context.IncludeAttachments && context.Note.Attachments.Count > 0)
        {
            List<string> unsupported = [];
            foreach (TickerNoteAttachmentDto attachment in context.Note.Attachments)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(attachment.DataBase64); }
                catch { unsupported.Add($"{attachment.Name} — corrupted attachment data"); continue; }

                try
                {
                    if (IsXlsxMimeType(attachment.MimeType))
                        AppendXlsxSheets(workbook, bytes, attachment.Name);
                    else if (TryGetPictureFormat(attachment.MimeType, out XLPictureFormat pf))
                        AppendImageSheet(workbook, bytes, pf, attachment.Name);
                    else
                        unsupported.Add($"{attachment.Name} ({attachment.MimeType})");
                }
                catch { unsupported.Add($"{attachment.Name} — could not be embedded"); }
            }
            if (unsupported.Count > 0) AddUnsupportedNote(ws, row, unsupported);
        }

        using MemoryStream stream = new();
        workbook.SaveAs(stream);
        string fileName = $"{context.Symbol}-{ReportFileNaming.Slugify(context.Note.Title)}.xlsx";
        return Task.FromResult(new JournalReportFile(stream.ToArray(), fileName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    // ── Blocks ─────────────────────────────────────────────────────────────

    private static int WriteCoverBlock(IXLWorksheet ws, int startRow, JournalReportContext context)
    {
        TickerDetail? d = context.TickerAnalysis;

        MergeAndSet(ws, startRow, 1, GridWidth, "SIDWELL · TRADING & FINANCIAL COCKPIT",
            fontSize: 8, bold: true, color: LightGray);
        ws.Row(startRow).Height = 16;

        int r = startRow + 1;
        MergeAndSet(ws, r, 1, GridWidth, context.Symbol,
            fontSize: 36, bold: true, color: BrandAccent);
        ws.Row(r).Height = 46;

        r++;
        if (d is not null && !string.IsNullOrWhiteSpace(d.Ticker.Name))
        {
            MergeAndSet(ws, r, 1, GridWidth, d.Ticker.Name, fontSize: 14, color: BodyText);
            ws.Row(r).Height = 20;
            r++;
        }

        if (d is not null)
        {
            string venue = string.Join("   ·   ", new[]
            {
                d.Price.Latest is null ? null : $"{ReportValueFormatter.Money(d.Price.Latest.Close, "")} {d.Ticker.Currency}",
                d.Ticker.Exchange,
                d.Ticker.Currency,
                d.Price.Latest?.Date,
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            MergeAndSet(ws, r, 1, GridWidth, venue, fontSize: 10, color: MutedGray);
            ws.Row(r).Height = 16;
            r++;
        }

        MergeAndSet(ws, r, 1, GridWidth,
            $"Generated by {context.AuthorName}   ·   {DateTimeOffset.Now:dd MMM yyyy, HH:mm}",
            fontSize: 8, color: LightGray);
        ws.Row(r).Height = 14;
        r++;

        DrawBottomBorder(ws, r, 1, GridWidth);
        return r + 2;
    }

    private static int WriteNoteBlock(IXLWorksheet ws, int startRow, JournalReportContext context)
    {
        int r = WriteSectionHeader(ws, startRow, context.Note.Title.ToUpperInvariant(), $"Created {context.Note.CreatedAt:dd MMM yyyy}");

        foreach (TickerNoteSectionDto s in context.Note.Sections)
        {
            if (string.IsNullOrWhiteSpace(s.Content)) continue;
            IXLRange range = ws.Range(r, 1, r, GridWidth).Merge();
            range.Value = s.Content;
            range.Style.Font.FontSize = 10;
            range.Style.Font.FontColor = BodyText;
            range.Style.Alignment.WrapText = true;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Row(r).Height = Math.Max(30, Math.Min(200, s.Content.Length / 6));
            r++;
        }
        return r + 2;
    }

    private static int WriteVerdictHero(IXLWorksheet ws, int startRow, CompositeScore composite)
    {
        int r = WriteSectionHeader(ws, startRow, "Composite verdict", "Weighted synthesis of applicable quantitative algorithms");

        XLColor accent = ResolveHexColor(composite.Color, BrandAccent);
        XLColor soft = XLColor.FromHtml(SoftenHex(composite.Color));

        // Left block: big score
        IXLRange left = ws.Range(r, 1, r + 2, 5).Merge();
        left.Value = $"{ReportValueFormatter.Auto(composite.Score)}  / 10";
        left.Style.Fill.BackgroundColor = accent;
        left.Style.Font.FontSize = 26;
        left.Style.Font.Bold = true;
        left.Style.Font.FontColor = XLColor.White;
        left.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        left.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Right block: label + description
        IXLRange rightLabel = ws.Range(r, 6, r, GridWidth).Merge();
        rightLabel.Value = composite.Label.ToUpperInvariant();
        rightLabel.Style.Fill.BackgroundColor = soft;
        rightLabel.Style.Font.FontSize = 14;
        rightLabel.Style.Font.Bold = true;
        rightLabel.Style.Font.FontColor = accent;
        rightLabel.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        rightLabel.Style.Alignment.Indent = 1;

        IXLRange rightPhilosophy = ws.Range(r + 1, 6, r + 1, GridWidth).Merge();
        rightPhilosophy.Value = $"Philosophy · {composite.Philosophy}";
        rightPhilosophy.Style.Fill.BackgroundColor = soft;
        rightPhilosophy.Style.Font.FontSize = 9;
        rightPhilosophy.Style.Font.FontColor = MutedGray;
        rightPhilosophy.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        rightPhilosophy.Style.Alignment.Indent = 1;

        IXLRange rightDesc = ws.Range(r + 2, 6, r + 2, GridWidth).Merge();
        rightDesc.Value = composite.Overridden
            ? "This score reflects a user override on top of the model output."
            : "Directly from the algorithm engine — no manual override.";
        rightDesc.Style.Fill.BackgroundColor = soft;
        rightDesc.Style.Font.FontSize = 9;
        rightDesc.Style.Font.FontColor = BodyText;
        rightDesc.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        rightDesc.Style.Alignment.Indent = 1;

        for (int i = 0; i < 3; i++) ws.Row(r + i).Height = 26;

        return r + 4;
    }

    private static int WriteKeyStatsGrid(IXLWorksheet ws, int startRow, TickerDetail d)
    {
        List<(string, string, XLColor?)> stats = new();
        if (d.Price.Latest is not null)
            stats.Add(("Latest close", ReportValueFormatter.Money(d.Price.Latest.Close, ""), BrandAccent));

        KeyStatsDto? k = d.KeyStats;
        if (k is not null)
        {
            if (k.MarketCap is not null) stats.Add(("Market cap", ReportValueFormatter.Money(k.MarketCap, "$"), null));
            if (k.FiftyTwoWeekLow is not null) stats.Add(("52W low", ReportValueFormatter.Money(k.FiftyTwoWeekLow, "$"), null));
            if (k.FiftyTwoWeekHigh is not null) stats.Add(("52W high", ReportValueFormatter.Money(k.FiftyTwoWeekHigh, "$"), null));
            if (k.PeTrailing is not null) stats.Add(("P/E (trailing)", ReportValueFormatter.Auto(k.PeTrailing), null));
            if (k.PriceToBook is not null) stats.Add(("Price / Book", ReportValueFormatter.Auto(k.PriceToBook), null));
            if (k.Beta is not null) stats.Add(("Beta", ReportValueFormatter.Auto(k.Beta), null));
            if (k.RoeTtm is not null) stats.Add(("ROE (TTM)", ReportValueFormatter.Auto(k.RoeTtm), null));
            if (k.DebtToEquity is not null) stats.Add(("Debt / Equity", ReportValueFormatter.Auto(k.DebtToEquity), null));
            if (k.RevenueGrowthTtmYoy is not null) stats.Add(("Revenue growth YoY", ReportValueFormatter.Auto(k.RevenueGrowthTtmYoy), null));
            if (k.EvToEbitda is not null) stats.Add(("EV / EBITDA", ReportValueFormatter.Auto(k.EvToEbitda), null));
            if (k.TargetOneYear is not null) stats.Add(("1Y target", ReportValueFormatter.Money(k.TargetOneYear, "$"), null));
            if (k.EarningsDate is not null) stats.Add(("Earnings date", k.EarningsDate, null));
            if (!string.IsNullOrWhiteSpace(k.AnalystConsensus)) stats.Add(("Analyst consensus", k.AnalystConsensus.ToUpperInvariant(), ResolveAnalystColor(k.AnalystConsensus)));
        }

        if (stats.Count == 0) return startRow;

        int r = WriteSectionHeader(ws, startRow, "Key statistics", "Snapshot from the latest sync");
        return WriteCardGrid(ws, r, stats, cardsPerRow: 4);
    }

    private static int WriteAlgorithmsGrid(IXLWorksheet ws, int startRow, IReadOnlyList<AlgoScore> algos)
    {
        List<AlgoScore> applicable = algos.Where(a => a.Applicable).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        List<AlgoScore> notApplicable = algos.Where(a => !a.Applicable).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        int r = WriteSectionHeader(ws, startRow, "Algorithms", "Quantitative model outputs");

        int cardsPerRow = 3;
        int cardWidth = GridWidth / cardsPerRow;

        for (int i = 0; i < applicable.Count; i += cardsPerRow)
        {
            int cardHeightRows = 3;
            for (int j = 0; j < cardsPerRow && (i + j) < applicable.Count; j++)
            {
                AlgoScore a = applicable[i + j];
                XLColor accent = ResolveAlgoScoreColor(a.Score);
                int c0 = j * cardWidth + 1;
                int c1 = c0 + cardWidth - 1;

                IXLRange nameRange = ws.Range(r, c0, r, c1 - 1).Merge();
                nameRange.Value = FormatAlgoName(a.Name);
                nameRange.Style.Fill.BackgroundColor = CardBg;
                nameRange.Style.Font.FontSize = 10;
                nameRange.Style.Font.Bold = true;
                nameRange.Style.Font.FontColor = BrandNavy;
                nameRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                nameRange.Style.Alignment.Indent = 1;

                IXLCell scoreCell = ws.Cell(r, c1);
                scoreCell.Value = ReportValueFormatter.Auto(a.Score);
                scoreCell.Style.Fill.BackgroundColor = CardBg;
                scoreCell.Style.Font.FontSize = 13;
                scoreCell.Style.Font.Bold = true;
                scoreCell.Style.Font.FontColor = accent;
                scoreCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                scoreCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                string interpretation = ExtractAlgoInterpretation(a);
                IXLRange interpRange = ws.Range(r + 1, c0, r + 1, c1).Merge();
                interpRange.Value = string.IsNullOrWhiteSpace(interpretation) ? " " : interpretation;
                interpRange.Style.Fill.BackgroundColor = CardBg;
                interpRange.Style.Font.FontSize = 8;
                interpRange.Style.Font.FontColor = MutedGray;
                interpRange.Style.Alignment.Indent = 1;
                interpRange.Style.Alignment.WrapText = true;
                interpRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

                DrawLeftAccent(ws, r, c0, r + cardHeightRows - 2, accent);
                DrawCardBorder(ws, r, c0, r + cardHeightRows - 2, c1);
            }
            ws.Row(r).Height = 22;
            ws.Row(r + 1).Height = 30;
            ws.Row(r + 2).Height = 6;
            r += cardHeightRows;
        }

        if (notApplicable.Count > 0)
        {
            IXLRange na = ws.Range(r, 1, r, GridWidth).Merge();
            na.Value = "Not applicable on this ticker: " + string.Join(", ", notApplicable.Select(a => FormatAlgoName(a.Name)));
            na.Style.Font.FontSize = 8;
            na.Style.Font.Italic = true;
            na.Style.Font.FontColor = MutedGray;
            ws.Row(r).Height = 20;
            r += 2;
        }
        return r + 1;
    }

    private static int WriteGatedList(IXLWorksheet ws, int startRow, IReadOnlyList<GatedAlgo> gated)
    {
        int r = WriteSectionHeader(ws, startRow, "Gated algorithms", "Not enough data on this ticker to run these");
        bool zebra = false;
        foreach (GatedAlgo g in gated)
        {
            IXLRange nameRange = ws.Range(r, 1, r, 4).Merge();
            nameRange.Value = FormatAlgoName(g.AlgoName);
            nameRange.Style.Font.FontSize = 9;
            nameRange.Style.Font.Bold = true;
            nameRange.Style.Font.FontColor = BrandNavy;
            nameRange.Style.Fill.BackgroundColor = zebra ? CardBgAlt : CardBg;
            nameRange.Style.Alignment.Indent = 1;
            nameRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            IXLRange reasonRange = ws.Range(r, 5, r, GridWidth).Merge();
            reasonRange.Value = g.MissingData;
            reasonRange.Style.Font.FontSize = 9;
            reasonRange.Style.Font.FontColor = MutedGray;
            reasonRange.Style.Fill.BackgroundColor = zebra ? CardBgAlt : XLColor.White;
            reasonRange.Style.Alignment.Indent = 1;
            reasonRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            reasonRange.Style.Alignment.WrapText = true;

            ws.Row(r).Height = 20;
            zebra = !zebra;
            r++;
        }
        return r + 2;
    }

    private static int WriteDividendsBlock(IXLWorksheet ws, int startRow, DividendInfoDto d)
    {
        int r = WriteSectionHeader(ws, startRow, "Dividends", null);
        List<(string, string, XLColor?)> facts = ReportSectionData.BuildDividendFacts(d)
            .Where(f => f.Value != "—")
            .Select(f => (f.Label, ReportValueFormatter.Auto(f.Value), (XLColor?)null))
            .ToList();
        return WriteCardGrid(ws, r, facts, cardsPerRow: 3);
    }

    private static int WriteHoldingBlock(IXLWorksheet ws, int startRow, HoldingDto h)
    {
        int r = WriteSectionHeader(ws, startRow, "Your holding", "Position on this ticker");
        List<(string, string, XLColor?)> facts = ReportSectionData.BuildHoldingFacts(h)
            .Select(f => (f.Label, ReportValueFormatter.Auto(f.Value), (XLColor?)null))
            .ToList();
        return WriteCardGrid(ws, r, facts, cardsPerRow: 3);
    }

    private static int WriteFundamentalsBlock(IXLWorksheet ws, int startRow, IReadOnlyList<FundamentalPeriod> periods)
    {
        int r = WriteSectionHeader(ws, startRow, "Fundamentals", "Most recent reported periods");

        List<FundamentalPeriod> ordered = periods.OrderByDescending(p => p.AsOfDate, StringComparer.Ordinal).Take(4).ToList();
        int cardWidth = GridWidth / ordered.Count;

        (string label, Func<FundamentalPeriod, string> get)[] lines =
        [
            ("Revenue",       p => ReportValueFormatter.LargeNumber(p.Revenue)),
            ("Net income",    p => ReportValueFormatter.LargeNumber(p.NetIncome)),
            ("Gross profit",  p => ReportValueFormatter.LargeNumber(p.GrossProfit)),
            ("EBIT",          p => ReportValueFormatter.LargeNumber(p.Ebit)),
            ("Total assets",  p => ReportValueFormatter.LargeNumber(p.TotalAssets)),
            ("Total equity",  p => ReportValueFormatter.LargeNumber(p.TotalEquity)),
            ("EPS",           p => ReportValueFormatter.Auto(p.Eps)),
            ("Shares",        p => ReportValueFormatter.LargeNumber(p.SharesOutstanding)),
        ];

        // Header per card (period label)
        for (int j = 0; j < ordered.Count; j++)
        {
            int c0 = j * cardWidth + 1;
            int c1 = c0 + cardWidth - 1;
            IXLRange header = ws.Range(r, c0, r, c1).Merge();
            header.Value = $"{ordered[j].Period} · {ordered[j].AsOfDate}";
            header.Style.Font.FontSize = 9;
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Fill.BackgroundColor = BrandAccent;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        ws.Row(r).Height = 20;
        r++;

        // Line rows
        bool zebra = false;
        foreach ((string label, Func<FundamentalPeriod, string> get) in lines)
        {
            for (int j = 0; j < ordered.Count; j++)
            {
                int c0 = j * cardWidth + 1;
                int c1 = c0 + cardWidth - 1;
                XLColor bg = zebra ? CardBgAlt : CardBg;
                IXLRange labelRange = ws.Range(r, c0, r, c0 + (cardWidth / 2) - 1).Merge();
                labelRange.Value = label;
                labelRange.Style.Font.FontSize = 8;
                labelRange.Style.Font.FontColor = MutedGray;
                labelRange.Style.Fill.BackgroundColor = bg;
                labelRange.Style.Alignment.Indent = 1;
                labelRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                IXLRange valueRange = ws.Range(r, c0 + (cardWidth / 2), r, c1).Merge();
                valueRange.Value = get(ordered[j]);
                valueRange.Style.Font.FontSize = 9;
                valueRange.Style.Font.Bold = true;
                valueRange.Style.Font.FontColor = BodyText;
                valueRange.Style.Fill.BackgroundColor = bg;
                valueRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                valueRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            ws.Row(r).Height = 18;
            zebra = !zebra;
            r++;
        }
        return r + 2;
    }

    private static int WritePriceHistoryBlock(IXLWorksheet ws, int startRow, IReadOnlyList<PriceBar> history)
    {
        int r = WriteSectionHeader(ws, startRow, "Recent price history", "Last 30 sessions");

        string[] headers = ["Date", "Open", "High", "Low", "Close", "Volume"];
        int[] colSpans = [2, 2, 2, 2, 2, 2];

        int c = 1;
        for (int i = 0; i < headers.Length; i++)
        {
            IXLRange range = ws.Range(r, c, r, c + colSpans[i] - 1).Merge();
            range.Value = headers[i];
            range.Style.Fill.BackgroundColor = BrandAccent;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 9;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            c += colSpans[i];
        }
        ws.Row(r).Height = 18;
        r++;

        bool zebra = false;
        foreach (PriceBar b in history.OrderByDescending(x => x.Date, StringComparer.Ordinal).Take(30))
        {
            string[] values =
            [
                b.Date,
                ReportValueFormatter.Auto(b.Open),
                ReportValueFormatter.Auto(b.High),
                ReportValueFormatter.Auto(b.Low),
                ReportValueFormatter.Auto(b.Close),
                b.Volume.ToString("N0"),
            ];
            c = 1;
            XLColor bg = zebra ? CardBgAlt : XLColor.White;
            for (int i = 0; i < values.Length; i++)
            {
                IXLRange range = ws.Range(r, c, r, c + colSpans[i] - 1).Merge();
                range.Value = values[i];
                range.Style.Fill.BackgroundColor = bg;
                range.Style.Font.FontSize = 9;
                range.Style.Font.FontColor = BodyText;
                range.Style.Alignment.Horizontal = i == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
                range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                range.Style.Alignment.Indent = 1;
                c += colSpans[i];
            }
            ws.Row(r).Height = 16;
            zebra = !zebra;
            r++;
        }
        return r + 2;
    }

    private static int WriteNewsBlock(IXLWorksheet ws, int startRow, IReadOnlyList<NewsItem> news)
    {
        int r = WriteSectionHeader(ws, startRow, "Latest news", null);

        foreach (NewsItem n in news.Take(10))
        {
            IXLRange metaRange = ws.Range(r, 1, r, GridWidth).Merge();
            metaRange.Value = $"{n.Source}   ·   {n.PublishedAt}";
            metaRange.Style.Font.FontSize = 8;
            metaRange.Style.Font.FontColor = MutedGray;
            metaRange.Style.Fill.BackgroundColor = XLColor.White;
            metaRange.Style.Alignment.Indent = 1;

            IXLRange titleRange = ws.Range(r + 1, 1, r + 1, GridWidth).Merge();
            titleRange.Value = n.Title;
            titleRange.Style.Font.FontSize = 10;
            titleRange.Style.Font.FontColor = BrandNavy;
            titleRange.Style.Fill.BackgroundColor = XLColor.White;
            titleRange.Style.Alignment.Indent = 1;
            titleRange.Style.Alignment.WrapText = true;

            IXLRange sentimentRange = ws.Range(r + 2, 1, r + 2, GridWidth).Merge();
            sentimentRange.Value = string.IsNullOrWhiteSpace(n.Sentiment)
                ? " "
                : $"Sentiment · {n.Sentiment.ToUpperInvariant()}";
            sentimentRange.Style.Font.FontSize = 8;
            sentimentRange.Style.Font.Bold = true;
            sentimentRange.Style.Font.FontColor = ResolveSentimentColor(n.Sentiment);
            sentimentRange.Style.Fill.BackgroundColor = XLColor.White;
            sentimentRange.Style.Alignment.Indent = 1;

            DrawCardBorder(ws, r, 1, r + 2, GridWidth);
            ws.Row(r).Height = 14;
            ws.Row(r + 1).Height = 20;
            ws.Row(r + 2).Height = 14;
            r += 4;
        }
        return r;
    }

    // ── Shared card grid ──────────────────────────────────────────────────

    private static int WriteCardGrid(IXLWorksheet ws, int startRow, IReadOnlyList<(string Label, string Value, XLColor? Accent)> cards, int cardsPerRow)
    {
        if (cards.Count == 0) return startRow;
        int cardWidth = GridWidth / cardsPerRow;
        int r = startRow;

        for (int i = 0; i < cards.Count; i += cardsPerRow)
        {
            for (int j = 0; j < cardsPerRow && (i + j) < cards.Count; j++)
            {
                (string label, string value, XLColor? accent) = cards[i + j];
                int c0 = j * cardWidth + 1;
                int c1 = c0 + cardWidth - 1;

                IXLRange labelRange = ws.Range(r, c0, r, c1).Merge();
                labelRange.Value = label.ToUpperInvariant();
                labelRange.Style.Font.FontSize = 7;
                labelRange.Style.Font.Bold = true;
                labelRange.Style.Font.FontColor = MutedGray;
                labelRange.Style.Fill.BackgroundColor = CardBg;
                labelRange.Style.Alignment.Indent = 1;
                labelRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                IXLRange valueRange = ws.Range(r + 1, c0, r + 1, c1).Merge();
                valueRange.Value = value;
                valueRange.Style.Font.FontSize = 14;
                valueRange.Style.Font.Bold = true;
                valueRange.Style.Font.FontColor = accent ?? BrandNavy;
                valueRange.Style.Fill.BackgroundColor = CardBg;
                valueRange.Style.Alignment.Indent = 1;
                valueRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                DrawLeftAccent(ws, r, c0, r + 1, accent ?? BorderGray);
                DrawCardBorder(ws, r, c0, r + 1, c1);
            }
            ws.Row(r).Height = 16;
            ws.Row(r + 1).Height = 24;
            r += 3;
        }
        return r + 1;
    }

    // ── Section header ────────────────────────────────────────────────────

    private static int WriteSectionHeader(IXLWorksheet ws, int startRow, string title, string? subtitle)
    {
        IXLRange titleRange = ws.Range(startRow, 1, startRow, GridWidth).Merge();
        titleRange.Value = title;
        titleRange.Style.Font.FontSize = 14;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontColor = BrandNavy;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Bottom;
        ws.Row(startRow).Height = 22;

        int r = startRow + 1;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            IXLRange subRange = ws.Range(r, 1, r, GridWidth).Merge();
            subRange.Value = subtitle;
            subRange.Style.Font.FontSize = 9;
            subRange.Style.Font.FontColor = MutedGray;
            ws.Row(r).Height = 14;
            r++;
        }
        return r + 1;
    }

    // ── Borders / cosmetic ────────────────────────────────────────────────

    private static void MergeAndSet(IXLWorksheet ws, int row, int c0, int c1, string value, double fontSize, bool bold = false, XLColor? color = null)
    {
        IXLRange range = ws.Range(row, c0, row, c1).Merge();
        range.Value = value;
        range.Style.Font.FontSize = fontSize;
        range.Style.Font.Bold = bold;
        if (color is not null) range.Style.Font.FontColor = color;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void DrawBottomBorder(IXLWorksheet ws, int row, int c0, int c1)
    {
        IXLRange r = ws.Range(row, c0, row, c1);
        r.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        r.Style.Border.BottomBorderColor = BorderGray;
    }

    private static void DrawCardBorder(IXLWorksheet ws, int r0, int c0, int r1, int c1)
    {
        IXLRange range = ws.Range(r0, c0, r1, c1);
        range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        range.Style.Border.RightBorder = XLBorderStyleValues.Thin;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        range.Style.Border.TopBorderColor = BorderGray;
        range.Style.Border.RightBorderColor = BorderGray;
        range.Style.Border.BottomBorderColor = BorderGray;
    }

    private static void DrawLeftAccent(IXLWorksheet ws, int r0, int c0, int r1, XLColor accent)
    {
        IXLRange range = ws.Range(r0, c0, r1, c0);
        range.Style.Border.LeftBorder = XLBorderStyleValues.Thick;
        range.Style.Border.LeftBorderColor = accent;
    }

    private static void AddUnsupportedNote(IXLWorksheet ws, int startRow, List<string> items)
    {
        int r = WriteSectionHeader(ws, startRow + 1, "Attachments not embedded", "Download these individually from the note in the app");
        foreach (string item in items)
        {
            IXLRange range = ws.Range(r, 1, r, GridWidth).Merge();
            range.Value = "•  " + item;
            range.Style.Font.FontSize = 9;
            range.Style.Font.FontColor = MutedGray;
            range.Style.Alignment.Indent = 1;
            r++;
        }
    }

    // ── Attachment append helpers (unchanged) ────────────────────────────

    private static bool IsXlsxMimeType(string mimeType) =>
        mimeType is "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/vnd.ms-excel";

    private static bool TryGetPictureFormat(string mimeType, out XLPictureFormat format)
    {
        switch (mimeType.ToLowerInvariant())
        {
            case "image/png": format = XLPictureFormat.Png; return true;
            case "image/jpeg":
            case "image/jpg": format = XLPictureFormat.Jpeg; return true;
            case "image/gif": format = XLPictureFormat.Gif; return true;
            case "image/bmp": format = XLPictureFormat.Bmp; return true;
            case "image/tiff": format = XLPictureFormat.Tiff; return true;
            default: format = default; return false;
        }
    }

    private static void AppendXlsxSheets(XLWorkbook destination, byte[] bytes, string attachmentName)
    {
        using MemoryStream stream = new(bytes);
        using XLWorkbook source = new(stream);
        foreach (IXLWorksheet sourceSheet in source.Worksheets)
        {
            string name = MakeUniqueSheetName(destination, $"{attachmentName}-{sourceSheet.Name}");
            sourceSheet.CopyTo(destination, name);
        }
    }

    private static void AppendImageSheet(XLWorkbook destination, byte[] bytes, XLPictureFormat format, string attachmentName)
    {
        string name = MakeUniqueSheetName(destination, attachmentName);
        IXLWorksheet ws = destination.Worksheets.Add(name);
        using MemoryStream imageStream = new(bytes);
        ws.AddPicture(imageStream, format).MoveTo(ws.Cell(1, 1));
    }

    private static string MakeUniqueSheetName(XLWorkbook workbook, string desired)
    {
        string sanitized = SanitizeSheetName(desired);
        string candidate = sanitized;
        int suffix = 2;
        while (workbook.Worksheets.Any(w => string.Equals(w.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            string suffixText = $" ({suffix})";
            candidate = sanitized.Length + suffixText.Length > 31
                ? sanitized[..(31 - suffixText.Length)] + suffixText
                : sanitized + suffixText;
            suffix++;
        }
        return candidate;
    }

    private static string SanitizeSheetName(string name)
    {
        char[] invalid = ['\\', '/', '?', '*', '[', ']', ':'];
        char[] cleaned = name.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        string result = new(cleaned);
        return result.Length > 31 ? result[..31] : result;
    }

    // ── Color helpers ─────────────────────────────────────────────────────

    private static XLColor ResolveHexColor(string? hex, XLColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return XLColor.FromHtml(hex.StartsWith('#') ? hex : "#" + hex); }
        catch { return fallback; }
    }

    private static string SoftenHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !hex.StartsWith('#') || hex.Length != 7) return "#ECFDF5";
        int r = Convert.ToInt32(hex[1..3], 16);
        int g = Convert.ToInt32(hex[3..5], 16);
        int b = Convert.ToInt32(hex[5..7], 16);
        int mix(int v) => Math.Min(255, v + (int)((255 - v) * 0.88));
        return $"#{mix(r):X2}{mix(g):X2}{mix(b):X2}";
    }

    private static XLColor ResolveAlgoScoreColor(string? score)
    {
        if (!decimal.TryParse(score, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal v))
            return LightGray;
        if (v >= 7) return SuccessColor;
        if (v >= 4) return WarningColor;
        return DangerColor;
    }

    private static XLColor ResolveAnalystColor(string consensus)
    {
        string c = consensus.ToLowerInvariant();
        if (c.Contains("buy")) return SuccessColor;
        if (c.Contains("sell")) return DangerColor;
        return WarningColor;
    }

    private static XLColor ResolveSentimentColor(string? sentiment)
    {
        if (string.IsNullOrWhiteSpace(sentiment)) return LightGray;
        string s = sentiment.ToLowerInvariant();
        if (s.Contains("positive") || s.Contains("bull")) return SuccessColor;
        if (s.Contains("negative") || s.Contains("bear")) return DangerColor;
        return WarningColor;
    }

    private static string ExtractAlgoInterpretation(AlgoScore a)
    {
        if (a.Details is null) return string.Empty;
        foreach (string key in new[] { "interpretation", "zone", "flag", "note", "margin_of_safety" })
        {
            if (a.Details.TryGetValue(key, out object? val) && val is not null)
            {
                string text = val.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return string.Empty;
    }

    private static string FormatAlgoName(string raw)
    {
        return string.Join(' ', raw.Split(['_', '-'])
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

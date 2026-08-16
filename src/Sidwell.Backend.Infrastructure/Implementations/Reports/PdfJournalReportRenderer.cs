using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

public sealed class PdfJournalReportRenderer : IJournalReportRenderer
{
    private static readonly Color BrandNavy = Color.FromRgb(0x11, 0x18, 0x27);
    private static readonly Color BodyText = Color.FromRgb(0x1F, 0x29, 0x37);
    private static readonly Color BrandAccent = Color.FromRgb(0x05, 0x96, 0x69);
    private static readonly Color AccentSoft = Color.FromRgb(0xEC, 0xFD, 0xF5);
    private static readonly Color MutedGray = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color LightGray = Color.FromRgb(0x9C, 0xA3, 0xAF);
    private static readonly Color BorderGray = Color.FromRgb(0xE5, 0xE7, 0xEB);
    private static readonly Color CardBg = Color.FromRgb(0xF9, 0xFA, 0xFB);
    private static readonly Color CardBgAlt = Color.FromRgb(0xF3, 0xF4, 0xF6);
    private static readonly Color SuccessColor = Color.FromRgb(0x10, 0xB9, 0x81);
    private static readonly Color WarningColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color DangerColor = Color.FromRgb(0xEF, 0x44, 0x44);

    private const double PageContentWidth = 17.0; // A4 width (21cm) − 2cm margins each side

    static PdfJournalReportRenderer()
    {
        if (GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.FontResolver = new LiberationFontResolver();
    }

    public bool CanRender(ReportFormat format) => format == ReportFormat.Pdf;

    public Task<JournalReportFile> RenderAsync(JournalReportContext context, ReportFormat format, CancellationToken ct = default)
    {
        if (!CanRender(format))
            throw new NotSupportedException($"{nameof(PdfJournalReportRenderer)} only supports {nameof(ReportFormat.Pdf)}.");

        Document document = BuildDocument(context);

        PdfDocumentRenderer pdfRenderer = new() { Document = document };
        pdfRenderer.RenderDocument();

        PdfDocument pdfDoc = pdfRenderer.PdfDocument;

        if (context.IncludeAttachments)
        {
            foreach (TickerNoteAttachmentDto attachment in context.Note.Attachments)
                AppendAttachment(pdfDoc, attachment);
        }

        using MemoryStream stream = new();
        pdfDoc.Save(stream);

        string fileName = $"{context.Symbol}-{ReportFileNaming.Slugify(context.Note.Title)}.pdf";

        return Task.FromResult(new JournalReportFile(stream.ToArray(), fileName, "application/pdf"));
    }

    private static Document BuildDocument(JournalReportContext context)
    {
        Document document = new();
        document.Info.Title = $"{context.Symbol} — {context.Note.Title}";
        document.Info.Author = context.AuthorName;

        Style normal = document.Styles["Normal"] ?? document.Styles.AddStyle("Normal", "");
        normal.Font.Name = LiberationFontResolver.FamilyName;
        normal.Font.Size = 10;
        normal.Font.Color = BodyText;

        Section section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        AddCover(section, context);
        AddNoteCard(section, context.Note);

        if (context.TickerAnalysis is not null)
            AddTickerAnalysis(section, context.TickerAnalysis);

        if (context.IncludeAttachments && context.Note.Attachments.Count > 0)
            AddAttachmentsAppendix(section, context.Note.Attachments);

        AddFooter(section);

        return document;
    }

    // ── Cover ──────────────────────────────────────────────────────────────

    private static void AddCover(Section section, JournalReportContext context)
    {
        TickerDetail? d = context.TickerAnalysis;

        Paragraph brand = section.AddParagraph("SIDWELL · TRADING & FINANCIAL COCKPIT");
        brand.Format.Font.Size = 7.5;
        brand.Format.Font.Bold = true;
        brand.Format.Font.Color = LightGray;
        brand.Format.SpaceAfter = Unit.FromPoint(6);

        Paragraph symbol = section.AddParagraph(context.Symbol);
        symbol.Format.Font.Size = 42;
        symbol.Format.Font.Bold = true;
        symbol.Format.Font.Color = BrandAccent;
        symbol.Format.SpaceAfter = Unit.FromPoint(2);

        if (d is not null && !string.IsNullOrWhiteSpace(d.Ticker.Name))
        {
            Paragraph name = section.AddParagraph(d.Ticker.Name);
            name.Format.Font.Size = 13;
            name.Format.Font.Color = BodyText;
            name.Format.SpaceAfter = Unit.FromPoint(3);
        }

        if (d is not null)
        {
            string venue = string.Join(" · ", new[] { d.Ticker.Exchange, d.Ticker.Currency }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (d.Price.Latest is not null)
                venue = $"{ReportValueFormatter.Money(d.Price.Latest.Close, "")} {d.Ticker.Currency}   ·   {venue}   ·   {d.Price.Latest.Date}";
            Paragraph venueP = section.AddParagraph(venue);
            venueP.Format.Font.Size = 10;
            venueP.Format.Font.Color = MutedGray;
            venueP.Format.SpaceAfter = Unit.FromPoint(14);
        }

        Paragraph meta = section.AddParagraph(
            $"Generated by {context.AuthorName}   ·   {DateTimeOffset.Now:dd MMM yyyy, HH:mm}");
        meta.Format.Font.Size = 8;
        meta.Format.Font.Color = LightGray;
        meta.Format.SpaceAfter = Unit.FromPoint(4);
        meta.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);
        meta.Format.Borders.Bottom.Color = BorderGray;
        meta.Format.Borders.DistanceFromBottom = Unit.FromPoint(10);
    }

    // ── Note card ──────────────────────────────────────────────────────────

    private static void AddNoteCard(Section section, TickerNoteDto note)
    {
        Table card = section.AddTable();
        card.Borders.Width = Unit.FromPoint(0.5);
        card.Borders.Color = BorderGray;
        card.AddColumn(Unit.FromCentimeter(PageContentWidth));

        Row header = card.AddRow();
        header.Cells[0].AddParagraph(note.Title.ToUpperInvariant()).Format.Font.Bold = true;
        header.Cells[0].Shading.Color = AccentSoft;
        FormatCellPadding(header.Cells[0]);
        header.Cells[0].Format.Font.Color = BrandAccent;
        header.Cells[0].Format.Font.Size = 10;

        Row meta = card.AddRow();
        meta.Cells[0].AddParagraph($"Created {note.CreatedAt:dd MMM yyyy}").Format.Font.Size = 8;
        meta.Cells[0].Format.Font.Color = MutedGray;
        meta.Cells[0].Shading.Color = AccentSoft;
        FormatCellPadding(meta.Cells[0], top: 0, bottom: 6);

        foreach (TickerNoteSectionDto s in note.Sections)
        {
            if (string.IsNullOrWhiteSpace(s.Content)) continue;
            Row body = card.AddRow();
            body.Cells[0].Shading.Color = Colors.White;
            FormatCellPadding(body.Cells[0]);
            foreach (string line in s.Content.Split('\n'))
            {
                Paragraph p = body.Cells[0].AddParagraph(line);
                p.Format.Font.Size = 10;
                p.Format.LineSpacing = Unit.FromPoint(14);
                p.Format.LineSpacingRule = LineSpacingRule.Multiple;
            }
        }

        AddSpacer(section, 12);
    }

    // ── Ticker analysis (Apple-card style) ────────────────────────────────

    private static void AddTickerAnalysis(Section section, TickerDetail detail)
    {
        if (detail.Composite is not null)
            AddVerdictHeroCard(section, detail.Composite);

        byte[]? chartPng = ChartRenderer.RenderPriceChart(detail.Price.History);
        if (chartPng is not null)
            AddChartCard(section, chartPng);

        AddKeyStatsGrid(section, detail);

        AddSectionHeader(section, "Algorithms", "Quantitative model outputs");
        AddAlgorithmCards(section, detail.Algorithms);

        if (detail.GatedAlgos.Count > 0)
        {
            AddSectionHeader(section, "Gated algorithms", "Not enough data on this ticker to run");
            AddGatedList(section, detail.GatedAlgos);
        }

        if (detail.Dividends is not null)
        {
            AddSectionHeader(section, "Dividends", null);
            AddCardGrid(section, ReportSectionData.BuildDividendFacts(detail.Dividends), 3);
        }

        if (detail.Holding is not null)
        {
            AddSectionHeader(section, "Your holding", "Position on this ticker");
            AddCardGrid(section, ReportSectionData.BuildHoldingFacts(detail.Holding), 3);
        }

        if (detail.Fundamentals.Count > 0)
        {
            AddSectionHeader(section, "Fundamentals", "Most recent reported periods");
            AddFundamentalsCards(section, detail.Fundamentals);
        }

        if (detail.Price.History.Count > 0)
        {
            AddSectionHeader(section, "Recent price history", "Last 30 sessions");
            AddPriceHistoryCompact(section, detail.Price.History);
        }

        if (detail.News.Count > 0)
        {
            AddSectionHeader(section, "Latest news", null);
            AddNewsCards(section, detail.News);
        }
    }

    private static void AddSectionHeader(Section section, string title, string? subtitle)
    {
        AddSpacer(section, 14);
        Paragraph t = section.AddParagraph(title);
        t.Format.Font.Size = 16;
        t.Format.Font.Bold = true;
        t.Format.Font.Color = BrandNavy;
        t.Format.SpaceAfter = Unit.FromPoint(1);

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            Paragraph s = section.AddParagraph(subtitle);
            s.Format.Font.Size = 8.5;
            s.Format.Font.Color = MutedGray;
            s.Format.SpaceAfter = Unit.FromPoint(8);
        }
        else
        {
            AddSpacer(section, 4);
        }
    }

    private static void AddVerdictHeroCard(Section section, CompositeScore composite)
    {
        Color accent = ResolveVerdictColor(composite.Color);
        Color soft = SoftenColor(accent);

        Table card = section.AddTable();
        card.Borders.Width = 0;
        card.AddColumn(Unit.FromCentimeter(PageContentWidth * 0.42));
        card.AddColumn(Unit.FromCentimeter(PageContentWidth * 0.58));

        Row row = card.AddRow();
        row.Cells[0].Shading.Color = accent;
        row.Cells[1].Shading.Color = soft;
        FormatCellPadding(row.Cells[0], top: 14, bottom: 14, left: 18, right: 12);
        FormatCellPadding(row.Cells[1], top: 14, bottom: 14, left: 14, right: 14);

        Paragraph label = row.Cells[0].AddParagraph("COMPOSITE VERDICT");
        label.Format.Font.Size = 7.5;
        label.Format.Font.Bold = true;
        label.Format.Font.Color = Colors.White;
        label.Format.SpaceAfter = Unit.FromPoint(4);

        Paragraph big = row.Cells[0].AddParagraph(ReportValueFormatter.Auto(composite.Score));
        big.Format.Font.Size = 34;
        big.Format.Font.Bold = true;
        big.Format.Font.Color = Colors.White;
        big.Format.SpaceAfter = Unit.FromPoint(0);

        Paragraph phil = row.Cells[0].AddParagraph($"/ 10   ·   {composite.Philosophy}");
        phil.Format.Font.Size = 8;
        phil.Format.Font.Color = Colors.White;

        Paragraph verdictLabel = row.Cells[1].AddParagraph(composite.Label.ToUpperInvariant());
        verdictLabel.Format.Font.Size = 14;
        verdictLabel.Format.Font.Bold = true;
        verdictLabel.Format.Font.Color = accent;
        verdictLabel.Format.SpaceAfter = Unit.FromPoint(3);

        Paragraph desc = row.Cells[1].AddParagraph(
            composite.Overridden
                ? "This score reflects a user override on top of the model output."
                : "Weighted synthesis of the applicable quantitative algorithms below.");
        desc.Format.Font.Size = 9;
        desc.Format.Font.Color = BodyText;

        AddSpacer(section, 10);
    }

    private static void AddChartCard(Section section, byte[] chartPng)
    {
        Table card = section.AddTable();
        card.Borders.Width = Unit.FromPoint(0.5);
        card.Borders.Color = BorderGray;
        card.AddColumn(Unit.FromCentimeter(PageContentWidth));

        Row header = card.AddRow();
        header.Cells[0].Shading.Color = CardBg;
        FormatCellPadding(header.Cells[0], top: 8, bottom: 6);
        Paragraph t = header.Cells[0].AddParagraph("PRICE CHART");
        t.Format.Font.Size = 8;
        t.Format.Font.Bold = true;
        t.Format.Font.Color = MutedGray;

        Row body = card.AddRow();
        body.Cells[0].Shading.Color = BrandNavy;
        FormatCellPadding(body.Cells[0], top: 0, bottom: 0, left: 0, right: 0);

        Paragraph imgP = body.Cells[0].AddParagraph();
        string b64 = Convert.ToBase64String(chartPng);
        Image img = imgP.AddImage("base64:" + b64);
        img.Width = Unit.FromCentimeter(PageContentWidth);
        img.LockAspectRatio = true;

        AddSpacer(section, 10);
    }

    private static void AddKeyStatsGrid(Section section, TickerDetail detail)
    {
        List<(string, string, Color?)> cards = new();

        if (detail.Price.Latest is not null)
            cards.Add(("Latest close", ReportValueFormatter.Money(detail.Price.Latest.Close, ""), BrandAccent));

        KeyStatsDto? k = detail.KeyStats;
        if (k is not null)
        {
            if (k.MarketCap is not null) cards.Add(("Market cap", ReportValueFormatter.Money(k.MarketCap, "$"), null));
            if (k.FiftyTwoWeekLow is not null) cards.Add(("52W low", ReportValueFormatter.Money(k.FiftyTwoWeekLow, "$"), null));
            if (k.FiftyTwoWeekHigh is not null) cards.Add(("52W high", ReportValueFormatter.Money(k.FiftyTwoWeekHigh, "$"), null));
            if (k.PeTrailing is not null) cards.Add(("P/E (trailing)", ReportValueFormatter.Auto(k.PeTrailing), null));
            if (k.PriceToBook is not null) cards.Add(("Price / Book", ReportValueFormatter.Auto(k.PriceToBook), null));
            if (k.Beta is not null) cards.Add(("Beta", ReportValueFormatter.Auto(k.Beta), null));
            if (k.RoeTtm is not null) cards.Add(("ROE (TTM)", ReportValueFormatter.Auto(k.RoeTtm), null));
            if (k.DebtToEquity is not null) cards.Add(("Debt / Equity", ReportValueFormatter.Auto(k.DebtToEquity), null));
            if (k.RevenueGrowthTtmYoy is not null) cards.Add(("Revenue growth YoY", ReportValueFormatter.Auto(k.RevenueGrowthTtmYoy), null));
            if (k.EvToEbitda is not null) cards.Add(("EV / EBITDA", ReportValueFormatter.Auto(k.EvToEbitda), null));
            if (k.TargetOneYear is not null) cards.Add(("1Y target", ReportValueFormatter.Money(k.TargetOneYear, "$"), null));
            if (k.EarningsDate is not null) cards.Add(("Earnings date", k.EarningsDate, null));
            if (!string.IsNullOrWhiteSpace(k.AnalystConsensus)) cards.Add(("Analyst consensus", k.AnalystConsensus.ToUpperInvariant(), ResolveAnalystColor(k.AnalystConsensus)));
        }

        if (cards.Count == 0) return;

        AddSectionHeader(section, "Key statistics", "Snapshot from the latest sync");
        AddColoredCardGrid(section, cards, 3);
    }

    private static void AddCardGrid(Section section, IReadOnlyList<(string Label, string Value)> facts, int columns)
    {
        List<(string, string, Color?)> withColors = facts
            .Where(f => f.Value != "—" && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => (f.Label, ReportValueFormatter.Auto(f.Value), (Color?)null))
            .ToList();
        if (withColors.Count == 0) return;
        AddColoredCardGrid(section, withColors, columns);
    }

    private static void AddColoredCardGrid(Section section, IReadOnlyList<(string Label, string Value, Color? Accent)> cards, int columns)
    {
        int rows = (int)Math.Ceiling(cards.Count / (double)columns);
        double colW = PageContentWidth / columns;

        Table grid = section.AddTable();
        grid.Borders.Width = 0;
        for (int c = 0; c < columns; c++)
            grid.AddColumn(Unit.FromCentimeter(colW));

        for (int r = 0; r < rows; r++)
        {
            Row row = grid.AddRow();
            for (int c = 0; c < columns; c++)
            {
                int idx = r * columns + c;
                Cell cell = row.Cells[c];
                if (idx >= cards.Count)
                {
                    cell.Shading.Color = Colors.White;
                    continue;
                }
                (string label, string value, Color? accent) = cards[idx];

                cell.Shading.Color = CardBg;
                cell.Borders.Left.Width = Unit.FromPoint(2);
                cell.Borders.Left.Color = accent ?? BorderGray;
                cell.Borders.Top.Width = Unit.FromPoint(0.25);
                cell.Borders.Right.Width = Unit.FromPoint(0.25);
                cell.Borders.Bottom.Width = Unit.FromPoint(0.25);
                cell.Borders.Top.Color = BorderGray;
                cell.Borders.Right.Color = BorderGray;
                cell.Borders.Bottom.Color = BorderGray;
                FormatCellPadding(cell, top: 8, bottom: 8, left: 10, right: 8);

                Paragraph l = cell.AddParagraph(label.ToUpperInvariant());
                l.Format.Font.Size = 7;
                l.Format.Font.Bold = true;
                l.Format.Font.Color = MutedGray;
                l.Format.SpaceAfter = Unit.FromPoint(3);

                Paragraph v = cell.AddParagraph(value);
                v.Format.Font.Size = 13;
                v.Format.Font.Bold = true;
                v.Format.Font.Color = accent ?? BrandNavy;
            }
        }
        AddSpacer(section, 8);
    }

    private static void AddAlgorithmCards(Section section, IReadOnlyList<AlgoScore> algos)
    {
        List<AlgoScore> applicable = algos.Where(a => a.Applicable).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        List<AlgoScore> notApplicable = algos.Where(a => !a.Applicable).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        const int columns = 2;
        int total = applicable.Count;
        int rows = (int)Math.Ceiling(total / (double)columns);
        double colW = PageContentWidth / columns;

        if (total > 0)
        {
            Table grid = section.AddTable();
            grid.Borders.Width = 0;
            for (int c = 0; c < columns; c++) grid.AddColumn(Unit.FromCentimeter(colW));

            for (int r = 0; r < rows; r++)
            {
                Row row = grid.AddRow();
                for (int c = 0; c < columns; c++)
                {
                    int idx = r * columns + c;
                    Cell cell = row.Cells[c];
                    if (idx >= total) { cell.Shading.Color = Colors.White; continue; }
                    AlgoScore a = applicable[idx];
                    Color accent = ResolveAlgoScoreColor(a.Score);

                    cell.Shading.Color = CardBg;
                    cell.Borders.Left.Width = Unit.FromPoint(2.5);
                    cell.Borders.Left.Color = accent;
                    cell.Borders.Top.Width = Unit.FromPoint(0.25);
                    cell.Borders.Right.Width = Unit.FromPoint(0.25);
                    cell.Borders.Bottom.Width = Unit.FromPoint(0.25);
                    cell.Borders.Top.Color = BorderGray;
                    cell.Borders.Right.Color = BorderGray;
                    cell.Borders.Bottom.Color = BorderGray;
                    FormatCellPadding(cell, top: 8, bottom: 8, left: 10, right: 10);

                    Table inner = cell.Elements.AddTable();
                    inner.Borders.Width = 0;
                    inner.AddColumn(Unit.FromCentimeter(colW - 3));
                    inner.AddColumn(Unit.FromCentimeter(2));
                    Row iRow = inner.AddRow();

                    Paragraph name = iRow.Cells[0].AddParagraph(FormatAlgoName(a.Name));
                    name.Format.Font.Size = 10;
                    name.Format.Font.Bold = true;
                    name.Format.Font.Color = BrandNavy;

                    Paragraph score = iRow.Cells[1].AddParagraph(ReportValueFormatter.Auto(a.Score));
                    score.Format.Font.Size = 14;
                    score.Format.Font.Bold = true;
                    score.Format.Font.Color = accent;
                    score.Format.Alignment = ParagraphAlignment.Right;

                    string interpretation = ExtractAlgoInterpretation(a);
                    if (!string.IsNullOrWhiteSpace(interpretation))
                    {
                        Paragraph note = cell.AddParagraph(interpretation);
                        note.Format.Font.Size = 8;
                        note.Format.Font.Color = MutedGray;
                        note.Format.SpaceBefore = Unit.FromPoint(3);
                    }
                }
            }
        }

        if (notApplicable.Count > 0)
        {
            AddSpacer(section, 4);
            Paragraph note = section.AddParagraph(
                "Not applicable on this ticker: " + string.Join(", ", notApplicable.Select(a => FormatAlgoName(a.Name))));
            note.Format.Font.Size = 8;
            note.Format.Font.Italic = true;
            note.Format.Font.Color = MutedGray;
        }
        AddSpacer(section, 8);
    }

    private static void AddGatedList(Section section, IReadOnlyList<GatedAlgo> gated)
    {
        Table t = section.AddTable();
        t.Borders.Width = Unit.FromPoint(0.25);
        t.Borders.Color = BorderGray;
        t.AddColumn(Unit.FromCentimeter(PageContentWidth * 0.4));
        t.AddColumn(Unit.FromCentimeter(PageContentWidth * 0.6));

        foreach (GatedAlgo g in gated)
        {
            Row row = t.AddRow();
            row.Cells[0].Shading.Color = CardBg;
            FormatCellPadding(row.Cells[0], top: 5, bottom: 5, left: 10, right: 6);
            Paragraph n = row.Cells[0].AddParagraph(FormatAlgoName(g.AlgoName));
            n.Format.Font.Size = 9;
            n.Format.Font.Bold = true;
            n.Format.Font.Color = BrandNavy;

            row.Cells[1].Shading.Color = Colors.White;
            FormatCellPadding(row.Cells[1], top: 5, bottom: 5, left: 10, right: 10);
            Paragraph m = row.Cells[1].AddParagraph(g.MissingData);
            m.Format.Font.Size = 8.5;
            m.Format.Font.Color = MutedGray;
        }
        AddSpacer(section, 8);
    }

    private static void AddFundamentalsCards(Section section, IReadOnlyList<FundamentalPeriod> periods)
    {
        List<FundamentalPeriod> ordered = periods
            .OrderByDescending(p => p.AsOfDate, StringComparer.Ordinal)
            .Take(4)
            .ToList();
        if (ordered.Count == 0) return;

        int cols = ordered.Count;
        double colW = PageContentWidth / cols;

        Table grid = section.AddTable();
        grid.Borders.Width = 0;
        for (int c = 0; c < cols; c++) grid.AddColumn(Unit.FromCentimeter(colW));

        Row row = grid.AddRow();
        for (int c = 0; c < cols; c++)
        {
            Cell cell = row.Cells[c];
            cell.Shading.Color = CardBg;
            cell.Borders.Left.Width = Unit.FromPoint(2);
            cell.Borders.Left.Color = BrandAccent;
            cell.Borders.Top.Width = Unit.FromPoint(0.25);
            cell.Borders.Right.Width = Unit.FromPoint(0.25);
            cell.Borders.Bottom.Width = Unit.FromPoint(0.25);
            cell.Borders.Top.Color = BorderGray;
            cell.Borders.Right.Color = BorderGray;
            cell.Borders.Bottom.Color = BorderGray;
            FormatCellPadding(cell, top: 10, bottom: 10, left: 10, right: 10);

            FundamentalPeriod p = ordered[c];
            Paragraph header = cell.AddParagraph($"{p.Period} · {p.AsOfDate}");
            header.Format.Font.Size = 7.5;
            header.Format.Font.Bold = true;
            header.Format.Font.Color = MutedGray;
            header.Format.SpaceAfter = Unit.FromPoint(6);

            AddFundLine(cell, "Revenue", ReportValueFormatter.LargeNumber(p.Revenue));
            AddFundLine(cell, "Net income", ReportValueFormatter.LargeNumber(p.NetIncome));
            AddFundLine(cell, "Gross profit", ReportValueFormatter.LargeNumber(p.GrossProfit));
            AddFundLine(cell, "EBIT", ReportValueFormatter.LargeNumber(p.Ebit));
            AddFundLine(cell, "Total assets", ReportValueFormatter.LargeNumber(p.TotalAssets));
            AddFundLine(cell, "Total equity", ReportValueFormatter.LargeNumber(p.TotalEquity));
            AddFundLine(cell, "EPS", ReportValueFormatter.Auto(p.Eps));
            AddFundLine(cell, "Shares", ReportValueFormatter.LargeNumber(p.SharesOutstanding));
        }
        AddSpacer(section, 8);
    }

    private static void AddFundLine(Cell cell, string label, string value)
    {
        Table t = cell.Elements.AddTable();
        t.Borders.Width = 0;
        t.AddColumn(Unit.FromCentimeter(2.2));
        t.AddColumn(Unit.FromCentimeter(1.8));
        Row r = t.AddRow();
        Paragraph l = r.Cells[0].AddParagraph(label);
        l.Format.Font.Size = 8;
        l.Format.Font.Color = MutedGray;
        Paragraph v = r.Cells[1].AddParagraph(value);
        v.Format.Font.Size = 8.5;
        v.Format.Font.Bold = true;
        v.Format.Font.Color = BodyText;
        v.Format.Alignment = ParagraphAlignment.Right;
    }

    private static void AddPriceHistoryCompact(Section section, IReadOnlyList<PriceBar> history)
    {
        List<PriceBar> rows = history.OrderByDescending(b => b.Date, StringComparer.Ordinal).Take(30).ToList();

        Table t = section.AddTable();
        t.Borders.Width = Unit.FromPoint(0.25);
        t.Borders.Color = BorderGray;
        double[] widths = [3, 3, 3, 3, 3, PageContentWidth - 15];
        foreach (double w in widths) t.AddColumn(Unit.FromCentimeter(w));

        Row header = t.AddRow();
        header.Shading.Color = BrandAccent;
        string[] headers = ["Date", "Open", "High", "Low", "Close", "Volume"];
        for (int i = 0; i < headers.Length; i++)
        {
            FormatCellPadding(header.Cells[i], top: 4, bottom: 4, left: 6, right: 6);
            Paragraph p = header.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Size = 8;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = Colors.White;
        }

        bool zebra = false;
        foreach (PriceBar b in rows)
        {
            Row r = t.AddRow();
            for (int i = 0; i < 6; i++)
            {
                r.Cells[i].Shading.Color = zebra ? CardBgAlt : Colors.White;
                FormatCellPadding(r.Cells[i], top: 3, bottom: 3, left: 6, right: 6);
            }
            string[] vals =
            [
                b.Date,
                ReportValueFormatter.Auto(b.Open),
                ReportValueFormatter.Auto(b.High),
                ReportValueFormatter.Auto(b.Low),
                ReportValueFormatter.Auto(b.Close),
                b.Volume.ToString("N0"),
            ];
            for (int i = 0; i < vals.Length; i++)
            {
                Paragraph p = r.Cells[i].AddParagraph(vals[i]);
                p.Format.Font.Size = 8;
                p.Format.Font.Color = BodyText;
            }
            zebra = !zebra;
        }
        AddSpacer(section, 8);
    }

    private static void AddNewsCards(Section section, IReadOnlyList<NewsItem> news)
    {
        foreach (NewsItem n in news.Take(10))
        {
            Table card = section.AddTable();
            card.Borders.Width = Unit.FromPoint(0.25);
            card.Borders.Color = BorderGray;
            card.AddColumn(Unit.FromCentimeter(PageContentWidth));

            Row row = card.AddRow();
            row.Cells[0].Shading.Color = Colors.White;
            FormatCellPadding(row.Cells[0], top: 8, bottom: 8, left: 12, right: 12);

            Color sentimentColor = ResolveSentimentColor(n.Sentiment);
            Paragraph meta = row.Cells[0].AddParagraph($"{n.Source}  ·  {n.PublishedAt}");
            meta.Format.Font.Size = 7.5;
            meta.Format.Font.Color = MutedGray;
            meta.Format.SpaceAfter = Unit.FromPoint(3);

            Paragraph title = row.Cells[0].AddParagraph(n.Title);
            title.Format.Font.Size = 9.5;
            title.Format.Font.Color = BrandNavy;
            title.Format.SpaceAfter = Unit.FromPoint(2);

            if (!string.IsNullOrWhiteSpace(n.Sentiment))
            {
                Paragraph s = row.Cells[0].AddParagraph($"Sentiment: {n.Sentiment.ToUpperInvariant()}");
                s.Format.Font.Size = 7.5;
                s.Format.Font.Bold = true;
                s.Format.Font.Color = sentimentColor;
            }
            AddSpacer(section, 4);
        }
        AddSpacer(section, 8);
    }

    // ── Attachments appendix ──────────────────────────────────────────────

    private static void AddAttachmentsAppendix(Section section, IReadOnlyList<TickerNoteAttachmentDto> attachments)
    {
        AddSectionHeader(section, "Attachments", "Files embedded on the pages that follow");
        foreach (TickerNoteAttachmentDto a in attachments)
        {
            Paragraph p = section.AddParagraph($"•  {a.Name}   ({a.MimeType})");
            p.Format.Font.Size = 9;
            p.Format.Font.Color = MutedGray;
            p.Format.SpaceAfter = Unit.FromPoint(2);
        }
    }

    // ── Footer ────────────────────────────────────────────────────────────

    private static void AddFooter(Section section)
    {
        Paragraph footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = LightGray;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("Sidwell · Trading & Financial Cockpit    ·    Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void FormatCellPadding(Cell cell, double top = 8, double bottom = 8, double left = 10, double right = 10)
    {
        cell.Format.SpaceBefore = Unit.FromPoint(top);
        cell.Format.SpaceAfter = Unit.FromPoint(bottom);
        cell.Format.LeftIndent = Unit.FromPoint(left);
        cell.Format.RightIndent = Unit.FromPoint(right);
    }

    private static void AddSpacer(Section section, double points)
    {
        Paragraph p = section.AddParagraph(" ");
        p.Format.Font.Size = 1;
        p.Format.SpaceAfter = Unit.FromPoint(points);
    }

    private static Color ResolveVerdictColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith('#') && hex.Length == 7
            && int.TryParse(hex[1..3], System.Globalization.NumberStyles.HexNumber, null, out int r)
            && int.TryParse(hex[3..5], System.Globalization.NumberStyles.HexNumber, null, out int g)
            && int.TryParse(hex[5..7], System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
        return BrandAccent;
    }

    private static Color SoftenColor(Color c)
    {
        byte mix(uint v) => (byte)Math.Min(255, v + (255 - v) * 0.88);
        return Color.FromRgb(mix(c.R), mix(c.G), mix(c.B));
    }

    private static Color ResolveAlgoScoreColor(string? score)
    {
        if (!decimal.TryParse(score, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal v))
            return LightGray;
        if (v >= 7) return SuccessColor;
        if (v >= 4) return WarningColor;
        return DangerColor;
    }

    private static Color ResolveAnalystColor(string consensus)
    {
        string c = consensus.ToLowerInvariant();
        if (c.Contains("buy")) return SuccessColor;
        if (c.Contains("sell")) return DangerColor;
        return WarningColor;
    }

    private static Color ResolveSentimentColor(string? sentiment)
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

    // ── Attachment concatenation (unchanged from previous version) ────────

    private static void AppendAttachment(PdfDocument pdfDoc, TickerNoteAttachmentDto attachment)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(attachment.DataBase64); }
        catch { AppendNoticePage(pdfDoc, attachment.Name, "Attachment data was corrupted and could not be decoded."); return; }

        try
        {
            if (attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                AppendImagePage(pdfDoc, bytes);
            else if (string.Equals(attachment.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                AppendMergedPdfPages(pdfDoc, bytes);
            else if (IsXlsxMimeType(attachment.MimeType))
                AppendXlsxAsPages(pdfDoc, bytes, attachment.Name);
            else
                AppendNoticePage(pdfDoc, attachment.Name, $"File type '{attachment.MimeType}' isn't embeddable — download it separately from the note.");
        }
        catch
        {
            AppendNoticePage(pdfDoc, attachment.Name, "This file could not be embedded (it may be corrupted or in an unsupported variant of its format).");
        }
    }

    private static bool IsXlsxMimeType(string mimeType) =>
        mimeType is "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/vnd.ms-excel";

    private static void AppendImagePage(PdfDocument pdfDoc, byte[] bytes)
    {
        using MemoryStream imageStream = new(bytes);
        XImage image = XImage.FromStream(imageStream);

        PdfPage page = pdfDoc.AddPage();
        page.Size = PageSize.A4;

        using XGraphics gfx = XGraphics.FromPdfPage(page);
        double margin = 28;
        double maxW = page.Width.Point - margin * 2;
        double maxH = page.Height.Point - margin * 2;
        double scale = Math.Min(maxW / image.PointWidth, maxH / image.PointHeight);
        double w = image.PointWidth * scale;
        double h = image.PointHeight * scale;

        gfx.DrawImage(image, (page.Width.Point - w) / 2, (page.Height.Point - h) / 2, w, h);
    }

    private static void AppendMergedPdfPages(PdfDocument pdfDoc, byte[] bytes)
    {
        using MemoryStream sourceStream = new(bytes);
        using PdfDocument imported = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Import);

        foreach (PdfPage page in imported.Pages)
            pdfDoc.AddPage(page);
    }

    private static void AppendXlsxAsPages(PdfDocument pdfDoc, byte[] bytes, string attachmentName)
    {
        using MemoryStream xlsxStream = new(bytes);
        using XLWorkbook workbook = new(xlsxStream);

        foreach (IXLWorksheet worksheet in workbook.Worksheets)
        {
            Document sheetDoc = new();
            Style normal = sheetDoc.Styles["Normal"] ?? sheetDoc.Styles.AddStyle("Normal", "");
            normal.Font.Name = LiberationFontResolver.FamilyName;
            normal.Font.Size = 8;

            Section section = sheetDoc.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);

            Paragraph heading = section.AddParagraph($"{attachmentName} — {worksheet.Name}");
            heading.Format.Font.Size = 11;
            heading.Format.Font.Bold = true;
            heading.Format.Font.Color = BrandAccent;
            heading.Format.SpaceAfter = Unit.FromPoint(8);

            IXLRange? used = worksheet.RangeUsed();
            if (used is null)
            {
                Paragraph empty = section.AddParagraph("(empty sheet)");
                empty.Format.Font.Italic = true;
                empty.Format.Font.Color = MutedGray;
            }
            else
            {
                int cols = used.ColumnCount();
                Table table = section.AddTable();
                for (int c = 0; c < cols; c++)
                    table.AddColumn(Unit.FromCentimeter(24.0 / Math.Max(cols, 1)));

                bool firstRow = true;
                foreach (IXLRangeRow row in used.RowsUsed())
                {
                    Row tr = table.AddRow();
                    if (firstRow) tr.Shading.Color = BrandAccent;

                    for (int c = 1; c <= cols; c++)
                    {
                        Paragraph cellP = tr.Cells[c - 1].AddParagraph(row.Cell(c).GetFormattedString());
                        cellP.Format.Font.Size = 7.5;
                        if (firstRow)
                        {
                            cellP.Format.Font.Bold = true;
                            cellP.Format.Font.Color = Colors.White;
                        }
                    }
                    firstRow = false;
                }
            }

            PdfDocumentRenderer sheetRenderer = new() { Document = sheetDoc };
            sheetRenderer.RenderDocument();

            using MemoryStream sheetStream = new();
            sheetRenderer.PdfDocument.Save(sheetStream);
            sheetStream.Position = 0;

            using PdfDocument importedSheet = PdfReader.Open(sheetStream, PdfDocumentOpenMode.Import);
            foreach (PdfPage page in importedSheet.Pages)
                pdfDoc.AddPage(page);
        }
    }

    private static void AppendNoticePage(PdfDocument pdfDoc, string attachmentName, string reason)
    {
        Document noticeDoc = new();
        Style normal = noticeDoc.Styles["Normal"] ?? noticeDoc.Styles.AddStyle("Normal", "");
        normal.Font.Name = LiberationFontResolver.FamilyName;

        Section section = noticeDoc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;

        Paragraph title = section.AddParagraph(attachmentName);
        title.Format.Font.Size = 12;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = BrandNavy;
        title.Format.SpaceAfter = Unit.FromPoint(6);

        Paragraph note = section.AddParagraph(reason);
        note.Format.Font.Size = 9.5;
        note.Format.Font.Italic = true;
        note.Format.Font.Color = MutedGray;

        PdfDocumentRenderer renderer = new() { Document = noticeDoc };
        renderer.RenderDocument();

        using MemoryStream stream = new();
        renderer.PdfDocument.Save(stream);
        stream.Position = 0;

        using PdfDocument imported = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        foreach (PdfPage page in imported.Pages)
            pdfDoc.AddPage(page);
    }
}

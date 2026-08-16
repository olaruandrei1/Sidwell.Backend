using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
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
    private static readonly Color BrandAccent = Color.FromRgb(0x05, 0x96, 0x69);
    private static readonly Color MutedGray = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color BorderGray = Color.FromRgb(0xE5, 0xE7, 0xEB);

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

    private static Document BuildDocument(JournalReportContext context)
    {
        Document document = new();
        document.Info.Title = $"{context.Symbol} — {context.Note.Title}";
        document.Info.Author = context.AuthorName;

        Style normal = document.Styles["Normal"] ?? document.Styles.AddStyle("Normal", "");
        normal.Font.Name = LiberationFontResolver.FamilyName;
        normal.Font.Size = 10.5;
        normal.Font.Color = BrandNavy;

        Section section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        AddCover(section, context);
        AddSections(section, context.Note);

        if (context.TickerAnalysis is not null)
            AddTickerAnalysis(section, context.TickerAnalysis);

        if (context.IncludeAttachments && context.Note.Attachments.Count > 0)
            AddAttachmentsAppendix(section, context.Note.Attachments);

        AddFooter(section);

        return document;
    }

    private static void AddTickerAnalysis(Section section, TickerDetail detail)
    {
        AddSectionTitle(section, "Ticker snapshot");
        AddFactsGrid(section, ReportSectionData.BuildHeaderFacts(detail));

        AddSectionTitle(section, "Composite verdict");
        AddFactsGrid(section, ReportSectionData.BuildCompositeFacts(detail.Composite));

        AddSectionTitle(section, "Key statistics");
        AddFactsGrid(section, ReportSectionData.BuildKeyStatsFacts(detail.KeyStats));

        AddSectionTitle(section, "Dividends");
        AddFactsGrid(section, ReportSectionData.BuildDividendFacts(detail.Dividends));

        if (detail.Holding is not null)
        {
            AddSectionTitle(section, "Your holding");
            AddFactsGrid(section, ReportSectionData.BuildHoldingFacts(detail.Holding));
        }

        AddReportTable(section, ReportSectionData.BuildAlgorithmsTable(detail.Algorithms));

        if (detail.GatedAlgos.Count > 0)
            AddReportTable(section, ReportSectionData.BuildGatedAlgosTable(detail.GatedAlgos));

        if (detail.Fundamentals.Count > 0)
            AddReportTable(section, ReportSectionData.BuildFundamentalsTable(detail.Fundamentals));

        if (detail.Price.History.Count > 0)
            AddReportTable(section, ReportSectionData.BuildPriceHistoryTable(detail.Price.History));

        if (detail.News.Count > 0)
            AddReportTable(section, ReportSectionData.BuildNewsTable(detail.News));
    }

    private static void AddSectionTitle(Section section, string title)
    {
        Paragraph heading = section.AddParagraph(title);
        heading.Format.Font.Size = 12;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = BrandAccent;
        heading.Format.SpaceBefore = Unit.FromPoint(16);
        heading.Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static void AddFactsGrid(Section section, IReadOnlyList<(string Label, string Value)> facts)
    {
        if (facts.Count == 0) return;

        Table table = section.AddTable();
        table.AddColumn(Unit.FromCentimeter(6));
        table.AddColumn(Unit.FromCentimeter(11));

        foreach ((string label, string value) in facts)
        {
            Row row = table.AddRow();
            Paragraph l = row.Cells[0].AddParagraph(label);
            l.Format.Font.Size = 9;
            l.Format.Font.Color = MutedGray;
            Paragraph v = row.Cells[1].AddParagraph(value);
            v.Format.Font.Size = 9.5;
            v.Format.Font.Color = BrandNavy;
        }
        table.Rows.LeftIndent = Unit.FromPoint(0);
    }

    private static void AddReportTable(Section section, ReportTable data)
    {
        AddSectionTitle(section, data.Title);

        Table table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(0.25);
        table.Borders.Color = BorderGray;

        double colWidth = 17.0 / data.Headers.Count;
        for (int i = 0; i < data.Headers.Count; i++)
            table.AddColumn(Unit.FromCentimeter(colWidth));

        Row headerRow = table.AddRow();
        headerRow.Shading.Color = BrandAccent;
        for (int i = 0; i < data.Headers.Count; i++)
        {
            Paragraph p = headerRow.Cells[i].AddParagraph(data.Headers[i]);
            p.Format.Font.Size = 8;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = Colors.White;
        }

        foreach (IReadOnlyList<string> rowData in data.Rows)
        {
            Row r = table.AddRow();
            for (int i = 0; i < data.Headers.Count && i < rowData.Count; i++)
            {
                Paragraph p = r.Cells[i].AddParagraph(rowData[i]);
                p.Format.Font.Size = 7.5;
                p.Format.Font.Color = BrandNavy;
            }
        }
    }

    private static void AddCover(Section section, JournalReportContext context)
    {
        Paragraph brand = section.AddParagraph("SIDWELL");
        brand.Format.Font.Size = 9;
        brand.Format.Font.Bold = true;
        brand.Format.Font.Color = MutedGray;
        brand.Format.SpaceAfter = Unit.FromPoint(2);

        Paragraph symbol = section.AddParagraph(context.Symbol);
        symbol.Format.Font.Size = 30;
        symbol.Format.Font.Bold = true;
        symbol.Format.Font.Color = BrandAccent;
        symbol.Format.SpaceAfter = Unit.FromPoint(4);

        Paragraph title = section.AddParagraph(context.Note.Title);
        title.Format.Font.Size = 15;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = BrandNavy;
        title.Format.SpaceAfter = Unit.FromPoint(6);

        Paragraph meta = section.AddParagraph(
            $"Generated by {context.AuthorName} · {DateTimeOffset.Now:dd MMM yyyy, HH:mm} · " +
            $"Note created {context.Note.CreatedAt:dd MMM yyyy}");
        meta.Format.Font.Size = 8.5;
        meta.Format.Font.Italic = true;
        meta.Format.Font.Color = MutedGray;
        meta.Format.SpaceAfter = Unit.FromPoint(10);
        meta.Format.Borders.Bottom.Width = Unit.FromPoint(1);
        meta.Format.Borders.Bottom.Color = BorderGray;
        meta.Format.Borders.DistanceFromBottom = Unit.FromPoint(10);
    }

    private static void AddSections(Section section, TickerNoteDto note)
    {
        bool multi = note.Sections.Count(s => !string.IsNullOrWhiteSpace(s.Content)) > 1;
        int index = 1;

        foreach (TickerNoteSectionDto s in note.Sections)
        {
            if (string.IsNullOrWhiteSpace(s.Content)) continue;

            if (multi)
            {
                Paragraph heading = section.AddParagraph($"Section {index}");
                heading.Format.Font.Size = 9;
                heading.Format.Font.Bold = true;
                heading.Format.Font.Color = BrandAccent;
                heading.Format.SpaceBefore = Unit.FromPoint(14);
                heading.Format.SpaceAfter = Unit.FromPoint(4);
                index++;
            }

            foreach (string line in s.Content.Split('\n'))
            {
                Paragraph body = section.AddParagraph(line);
                body.Format.Font.Size = 10.5;
                body.Format.SpaceAfter = Unit.FromPoint(2);
                body.Format.LineSpacing = Unit.FromPoint(14);
            }
        }
    }

    private static void AddAttachmentsAppendix(Section section, IReadOnlyList<TickerNoteAttachmentDto> attachments)
    {
        AddSectionTitle(section, "Attachments");

        foreach (TickerNoteAttachmentDto a in attachments)
        {
            Paragraph row = section.AddParagraph($"• {a.Name} ({a.MimeType})");
            row.Format.Font.Size = 9.5;
            row.Format.Font.Color = MutedGray;
            row.Format.SpaceAfter = Unit.FromPoint(2);
        }

        Paragraph note = section.AddParagraph("Full attachment content follows on the pages below.");
        note.Format.Font.Size = 8;
        note.Format.Font.Italic = true;
        note.Format.Font.Color = MutedGray;
        note.Format.SpaceBefore = Unit.FromPoint(6);
    }

    private static void AddFooter(Section section)
    {
        Paragraph footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = MutedGray;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("Sidwell — Trading & Financial Cockpit · Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }
}

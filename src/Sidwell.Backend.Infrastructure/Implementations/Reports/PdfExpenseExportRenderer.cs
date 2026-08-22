using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

public sealed class PdfExpenseExportRenderer : IExpenseExportRenderer
{
    private static readonly Color BrandNavy = Color.FromRgb(0x11, 0x18, 0x27);
    private static readonly Color BodyText = Color.FromRgb(0x1F, 0x29, 0x37);
    private static readonly Color BrandAccent = Color.FromRgb(0x05, 0x96, 0x69);
    private static readonly Color MutedGray = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color BorderGray = Color.FromRgb(0xE5, 0xE7, 0xEB);
    private static readonly Color CardBgAlt = Color.FromRgb(0xF3, 0xF4, 0xF6);

    private const double PageContentWidth = 17.0; // A4 width (21cm) − 2cm margins each side

    static PdfExpenseExportRenderer()
    {
        if (GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.FontResolver = new LiberationFontResolver();
    }

    public bool CanRender(ReportFormat format) => format == ReportFormat.Pdf;

    public Task<JournalReportFile> RenderAsync(
        IReadOnlyList<ExpenseExportRow> rows, string startMonth, string endMonth, CancellationToken ct = default)
    {
        Document document = new();
        document.Info.Title = "Cheltuieli";

        Style normal = document.Styles["Normal"] ?? document.Styles.AddStyle("Normal", "");
        normal.Font.Name = LiberationFontResolver.FamilyName;
        normal.Font.Size = 9;
        normal.Font.Color = BodyText;

        Section section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        Paragraph title = section.AddParagraph("Cheltuieli");
        title.Format.Font.Size = 18;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = BrandNavy;

        Paragraph subtitle = section.AddParagraph(startMonth == endMonth ? startMonth : $"{startMonth} — {endMonth}");
        subtitle.Format.Font.Size = 10;
        subtitle.Format.Font.Color = MutedGray;
        subtitle.Format.SpaceAfter = Unit.FromPoint(14);

        AddExpenseTable(section, rows);

        PdfDocumentRenderer pdfRenderer = new() { Document = document };
        pdfRenderer.RenderDocument();

        PdfDocument pdfDoc = pdfRenderer.PdfDocument;
        using MemoryStream stream = new();
        pdfDoc.Save(stream);

        string fileName = startMonth == endMonth ? $"cheltuieli_{startMonth}.pdf" : $"cheltuieli_{startMonth}_{endMonth}.pdf";

        return Task.FromResult(new JournalReportFile(stream.ToArray(), fileName, "application/pdf"));
    }

    private static void AddExpenseTable(Section section, IReadOnlyList<ExpenseExportRow> rows)
    {
        Table t = section.AddTable();
        t.Borders.Width = Unit.FromPoint(0.25);
        t.Borders.Color = BorderGray;
        double[] widths = [2, 4.5, 3, 2.5, 2.5, 1.5, 2, 2, 2.5];
        double scale = PageContentWidth / widths.Sum();
        foreach (double w in widths) t.AddColumn(Unit.FromCentimeter(w * scale));

        Row header = t.AddRow();
        header.Shading.Color = BrandAccent;
        string[] headers = ["Luna", "Nume", "Categorie", "Tip", "Sumă", "Monedă", "Status", "Scadență", "Recurent"];
        for (int i = 0; i < headers.Length; i++)
        {
            FormatCellPadding(header.Cells[i], top: 4, bottom: 4, left: 6, right: 6);
            Paragraph p = header.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Size = 8;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = Colors.White;
        }

        bool zebra = false;
        decimal total = 0m;
        foreach (ExpenseExportRow r in rows)
        {
            Row row = t.AddRow();
            for (int i = 0; i < headers.Length; i++)
            {
                row.Cells[i].Shading.Color = zebra ? CardBgAlt : Colors.White;
                FormatCellPadding(row.Cells[i], top: 3, bottom: 3, left: 6, right: 6);
            }

            string[] vals =
            [
                r.Month,
                r.Name,
                r.Category,
                r.Type,
                r.Amount.ToString("N2"),
                r.Currency,
                r.Status,
                r.DueDate?.ToString("yyyy-MM-dd") ?? "—",
                r.IsRecurring ? "Da" : "Nu",
            ];
            for (int i = 0; i < vals.Length; i++)
            {
                Paragraph p = row.Cells[i].AddParagraph(vals[i]);
                p.Format.Font.Size = 8;
                p.Format.Font.Color = BodyText;
            }
            total += r.Amount;
            zebra = !zebra;
        }

        if (rows.Count == 0)
        {
            Row empty = t.AddRow();
            empty.Cells[0].MergeRight = headers.Length - 1;
            Paragraph p = empty.Cells[0].AddParagraph("Nicio cheltuială în perioada selectată.");
            p.Format.Font.Size = 9;
            p.Format.Font.Color = MutedGray;
            p.Format.Alignment = ParagraphAlignment.Center;
            FormatCellPadding(empty.Cells[0], top: 10, bottom: 10, left: 6, right: 6);
        }
        else
        {
            Row totalRow = t.AddRow();
            Paragraph totalLabel = totalRow.Cells[3].AddParagraph("TOTAL");
            totalLabel.Format.Font.Size = 8;
            totalLabel.Format.Font.Bold = true;
            Paragraph totalValue = totalRow.Cells[4].AddParagraph(total.ToString("N2"));
            totalValue.Format.Font.Size = 8;
            totalValue.Format.Font.Bold = true;
            for (int i = 0; i < headers.Length; i++)
                FormatCellPadding(totalRow.Cells[i], top: 4, bottom: 4, left: 6, right: 6);
        }
    }

    private static void FormatCellPadding(Cell cell, double top, double bottom, double left, double right)
    {
        cell.Format.SpaceBefore = Unit.FromPoint(top);
        cell.Format.SpaceAfter = Unit.FromPoint(bottom);
        cell.Format.LeftIndent = Unit.FromPoint(left);
        cell.Format.RightIndent = Unit.FromPoint(right);
    }
}

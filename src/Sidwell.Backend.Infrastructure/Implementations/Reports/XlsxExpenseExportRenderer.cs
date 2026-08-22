using ClosedXML.Excel;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

public sealed class XlsxExpenseExportRenderer : IExpenseExportRenderer
{
    private static readonly XLColor HeaderBg = XLColor.FromHtml("#111827");
    private static readonly XLColor HeaderText = XLColor.FromHtml("#F9FAFB");
    private static readonly XLColor BorderGray = XLColor.FromHtml("#E5E7EB");
    private static readonly string[] Headers = ["Luna", "Nume", "Categorie", "Tip", "Sumă", "Monedă", "Status", "Scadență", "Recurent"];

    public bool CanRender(ReportFormat format) => format == ReportFormat.Xlsx;

    public Task<JournalReportFile> RenderAsync(
        IReadOnlyList<ExpenseExportRow> rows, string startMonth, string endMonth, CancellationToken ct = default)
    {
        using XLWorkbook workbook = new();
        IXLWorksheet ws = workbook.Worksheets.Add("Cheltuieli");
        ws.ShowGridLines = false;

        for (int c = 1; c <= Headers.Length; c++)
        {
            IXLCell cell = ws.Cell(1, c);
            cell.Value = Headers[c - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderText;
            cell.Style.Fill.BackgroundColor = HeaderBg;
        }

        int row = 2;
        decimal total = 0m;
        foreach (ExpenseExportRow r in rows)
        {
            ws.Cell(row, 1).Value = r.Month;
            ws.Cell(row, 2).Value = r.Name;
            ws.Cell(row, 3).Value = r.Category;
            ws.Cell(row, 4).Value = r.Type;
            ws.Cell(row, 5).Value = r.Amount;
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 6).Value = r.Currency;
            ws.Cell(row, 7).Value = r.Status;
            ws.Cell(row, 8).Value = r.DueDate?.ToString("yyyy-MM-dd") ?? "";
            ws.Cell(row, 9).Value = r.IsRecurring ? "Da" : "Nu";
            for (int c = 1; c <= Headers.Length; c++)
                ws.Cell(row, c).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            total += r.Amount;
            row++;
        }

        ws.Cell(row, 4).Value = "TOTAL";
        ws.Cell(row, 4).Style.Font.Bold = true;
        ws.Cell(row, 5).Value = total;
        ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 5).Style.Font.Bold = true;

        ws.Range(1, 1, row, Headers.Length).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(1, 1, row, Headers.Length).Style.Border.OutsideBorderColor = BorderGray;
        ws.Columns().AdjustToContents();

        using MemoryStream stream = new();
        workbook.SaveAs(stream);

        string fileName = startMonth == endMonth ? $"cheltuieli_{startMonth}.xlsx" : $"cheltuieli_{startMonth}_{endMonth}.xlsx";
        JournalReportFile file = new(
            stream.ToArray(),
            fileName,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        return Task.FromResult(file);
    }
}

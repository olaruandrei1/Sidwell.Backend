using System.Globalization;
using Sidwell.Backend.Application.Dtos;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

internal static class ChartRenderer
{
    private static readonly Color BgColor = Color.ParseHex("111827");
    private static readonly Color GridColor = Color.FromRgba(255, 255, 255, 20);
    private static readonly Color LineColor = Color.ParseHex("00E599");
    private static readonly Color SmaColor = Color.ParseHex("38BDF8");
    private static readonly Color TextColor = Color.ParseHex("94A3B8");

    public static byte[]? RenderPriceChart(IReadOnlyList<PriceBar> history, int width = 1000, int height = 320)
    {
        if (history.Count < 2) return null;

        List<PriceBar> bars = history
            .OrderBy(b => b.Date, StringComparer.Ordinal)
            .TakeLast(120)
            .ToList();

        List<double> closes = [];
        foreach (PriceBar b in bars)
        {
            if (double.TryParse(b.Close, NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
                closes.Add(c);
        }

        if (closes.Count < 2) return null;

        double min = closes.Min();
        double max = closes.Max();
        if (max - min < 0.0001) max = min + 1;
        double pad = (max - min) * 0.08;
        min -= pad;
        max += pad;

        double leftMargin = 60;
        double rightMargin = 20;
        double topMargin = 24;
        double bottomMargin = 40;
        double plotW = width - leftMargin - rightMargin;
        double plotH = height - topMargin - bottomMargin;

        using Image<Rgba32> img = new(width, height);
        img.Mutate(ctx =>
        {
            ctx.Fill(BgColor);

            for (int i = 0; i <= 4; i++)
            {
                float y = (float)(topMargin + plotH * i / 4);
                ctx.DrawLine(GridColor, 1, new PointF((float)leftMargin, y), new PointF((float)(leftMargin + plotW), y));
            }

            List<PointF> pricePoints = [];
            for (int i = 0; i < closes.Count; i++)
            {
                double x = leftMargin + plotW * i / (closes.Count - 1);
                double y = topMargin + plotH * (1 - (closes[i] - min) / (max - min));
                pricePoints.Add(new PointF((float)x, (float)y));
            }
            for (int i = 1; i < pricePoints.Count; i++)
                ctx.DrawLine(LineColor, 2, pricePoints[i - 1], pricePoints[i]);

            List<double> sma = ComputeSma(closes, 20);
            List<PointF> smaPoints = [];
            int offset = closes.Count - sma.Count;
            for (int i = 0; i < sma.Count; i++)
            {
                double x = leftMargin + plotW * (i + offset) / (closes.Count - 1);
                double y = topMargin + plotH * (1 - (sma[i] - min) / (max - min));
                smaPoints.Add(new PointF((float)x, (float)y));
            }
            for (int i = 1; i < smaPoints.Count; i++)
                ctx.DrawLine(SmaColor, 1.5f, smaPoints[i - 1], smaPoints[i]);

            try
            {
                Font font = SystemFonts.CreateFont("Liberation Sans", 10);
                for (int i = 0; i <= 4; i++)
                {
                    double value = max - (max - min) * i / 4;
                    float y = (float)(topMargin + plotH * i / 4 - 5);
                    ctx.DrawText(value.ToString("0.##", CultureInfo.InvariantCulture), font, TextColor, new PointF(6, y));
                }
                ctx.DrawText(bars.First().Date, font, TextColor, new PointF((float)leftMargin, (float)(height - 18)));
                ctx.DrawText(bars.Last().Date, font, TextColor, new PointF((float)(leftMargin + plotW - 70), (float)(height - 18)));
            }
            catch { /* fonts unavailable — chart still readable without axis labels */ }
        });

        using MemoryStream ms = new();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static List<double> ComputeSma(List<double> closes, int period)
    {
        if (closes.Count < period) return [];
        List<double> result = new(closes.Count - period + 1);
        double sum = 0;
        for (int i = 0; i < period; i++) sum += closes[i];
        result.Add(sum / period);
        for (int i = period; i < closes.Count; i++)
        {
            sum += closes[i] - closes[i - period];
            result.Add(sum / period);
        }
        return result;
    }
}

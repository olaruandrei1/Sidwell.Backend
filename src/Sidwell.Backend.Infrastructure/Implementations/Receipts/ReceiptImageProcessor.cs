using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Sidwell.Backend.Infrastructure.Implementations.Receipts;

public sealed class ReceiptImageProcessor(IOptions<FinanceOptions> options) : IReceiptImageProcessor
{
    private const string JpegMimeType = "image/jpeg";

    private readonly FinanceOptions _options = options.Value;

    public async Task<ProcessedReceiptImage> ProcessAndStoreAsync(Stream imageStream, Guid userId, CancellationToken ct = default)
    {
        using Image image = await Image.LoadAsync(imageStream, ct);

        int maxEdge = _options.ReceiptMaxLongEdgePx > 0 ? _options.ReceiptMaxLongEdgePx : 1600;

        int longEdge = Math.Max(image.Width, image.Height);

        if (longEdge > maxEdge)
        {
            double scale = (double)maxEdge / longEdge;

            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));

            image.Mutate(x => x.Resize(width, height));
        }

        int quality = _options.ReceiptJpegQuality is > 0 and <= 100 ? _options.ReceiptJpegQuality : 70;

        JpegEncoder encoder = new() { Quality = quality };

        using MemoryStream buffer = new();

        await image.SaveAsync(buffer, encoder, ct);

        byte[] bytes = buffer.ToArray();

        string directory = Path.Combine(_options.ReceiptStoragePath, userId.ToString());

        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"{Guid.NewGuid():N}.jpg");

        await File.WriteAllBytesAsync(filePath, bytes, ct);

        return new ProcessedReceiptImage(bytes, JpegMimeType, filePath);
    }
}

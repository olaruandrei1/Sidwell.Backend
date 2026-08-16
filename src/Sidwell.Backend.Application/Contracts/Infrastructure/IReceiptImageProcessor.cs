namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record ProcessedReceiptImage(
    byte[] Bytes,
    string MimeType,
    string FilePath
);

public interface IReceiptImageProcessor
{
    Task<ProcessedReceiptImage> ProcessAndStoreAsync(Stream imageStream, Guid userId, CancellationToken ct = default);
}

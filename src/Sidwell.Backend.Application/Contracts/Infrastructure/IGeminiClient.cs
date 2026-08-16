using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record GeminiBrokerFeeResult(
    decimal? Percent,
    decimal? MinFee,
    decimal? FixedFee,
    decimal? FxConversionPercent,
    string? Currency,
    string? Notes,
    string? SourceUrl
);

public sealed record GeminiDividendInfoResult(
    decimal? DividendYield,
    decimal? ForwardDividend,
    DateOnly? ExDividendDate,
    string? PayFrequency,
    decimal? HistGrowthCagr,
    string? SourceUrl
);

public sealed record GeminiReceiptItem(string? Name, int? Qty, decimal? UnitPrice, decimal? Amount);

public sealed record GeminiReceiptResult(
    string? Merchant,
    decimal? Total,
    DateOnly? Date,
    string? Category,
    IReadOnlyList<GeminiReceiptItem>? Items
);

public interface IGeminiClient
{
    Task<GeminiBrokerFeeResult?> FetchBrokerFeesAsync(Broker broker, string market, CancellationToken ct = default);

    Task<GeminiDividendInfoResult?> FetchDividendInfoAsync(string symbol, CancellationToken ct = default);

    Task<GeminiReceiptResult?> ParseReceiptAsync(byte[] image, string mimeType, CancellationToken ct = default);
}

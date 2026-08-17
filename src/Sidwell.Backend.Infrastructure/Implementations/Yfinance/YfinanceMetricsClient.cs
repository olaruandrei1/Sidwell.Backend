using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Infrastructure.Implementations.Yfinance;

// Fallback key-stats source for tickers Finnhub doesn't cover (e.g. BVB/.RO) — see
// Sidwell.Sync.YfinanceAdapter's KeyStatsView, which reads these fields from yfinance's .info dict.
public sealed class YfinanceMetricsClient(
    IHttpClientFactory httpClientFactory,
    ILogger<YfinanceMetricsClient> logger
) : IYfinanceMetricsClient
{
    public const string HttpClientName = "yfinance-metrics";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<YfinanceStockMetrics?> GetMetricsAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string url = $"api/v1/key-stats?symbol={Uri.EscapeDataString(symbol)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Yfinance /key-stats returned {Status} for {Symbol}", response.StatusCode, symbol);
                return null;
            }

            KeyStatsResponse? body = await response.Content.ReadFromJsonAsync<KeyStatsResponse>(JsonOptions, ct);
            if (body is null)
                return null;

            AnalystConsensus? consensus = body.AnalystBuy is not null || body.AnalystHold is not null || body.AnalystSell is not null
                ? new AnalystConsensus(body.AnalystBuy ?? 0, body.AnalystHold ?? 0, body.AnalystSell ?? 0, null)
                : null;

            return new YfinanceStockMetrics(
                body.Beta,
                body.TargetMeanPrice,
                FormatEarningsDate(body.EarningsTimestamp),
                body.TrailingPe,
                body.PriceToBook,
                // yfinance returns returnOnEquity/revenueGrowth as fractions (0.157 = 15.7%); Finnhub's
                // equivalent fields are already percentage-scaled, so scale these up to stay consistent.
                body.ReturnOnEquity is { } roe ? roe * 100m : null,
                body.DebtToEquity,
                body.RevenueGrowth is { } rg ? rg * 100m : null,
                body.EnterpriseToEbitda,
                body.MarketCap,
                consensus
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Yfinance /key-stats errored for {Symbol}; returning null.", symbol);
            return null;
        }
    }

    private static string? FormatEarningsDate(long? unixTimestamp) =>
        unixTimestamp is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd") : null;

    private sealed record KeyStatsResponse(
        [property: JsonPropertyName("trailingPE")] decimal? TrailingPe,
        [property: JsonPropertyName("priceToBook")] decimal? PriceToBook,
        [property: JsonPropertyName("returnOnEquity")] decimal? ReturnOnEquity,
        [property: JsonPropertyName("beta")] decimal? Beta,
        [property: JsonPropertyName("debtToEquity")] decimal? DebtToEquity,
        [property: JsonPropertyName("revenueGrowth")] decimal? RevenueGrowth,
        [property: JsonPropertyName("enterpriseToEbitda")] decimal? EnterpriseToEbitda,
        [property: JsonPropertyName("marketCap")] decimal? MarketCap,
        [property: JsonPropertyName("earningsTimestamp")] long? EarningsTimestamp,
        [property: JsonPropertyName("targetMeanPrice")] decimal? TargetMeanPrice,
        [property: JsonPropertyName("analystBuy")] int? AnalystBuy,
        [property: JsonPropertyName("analystHold")] int? AnalystHold,
        [property: JsonPropertyName("analystSell")] int? AnalystSell);
}

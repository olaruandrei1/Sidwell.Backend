using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.Finnhub;

public sealed class FinnhubMetricsClient(
    IHttpClientFactory httpClientFactory,
    IOptions<FinnhubOptions> options,
    ILogger<FinnhubMetricsClient> logger
) : IFinnhubMetricsClient
{
    public const string HttpClientName = "finnhub";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FinnhubOptions _options = options.Value;

    public async Task<FinnhubStockMetrics?> GetMetricsAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogWarning("Finnhub API key not configured; skipping metrics fetch.");
            return new FinnhubStockMetrics(null, null, null, null, null, null, null, null, null, null);
        }

        string encoded = Uri.EscapeDataString(symbol);

        Task<MetricResponse?> metricsTask = FetchMetricsAsync(encoded, ct);
        Task<decimal?> targetTask = FetchPriceTargetAsync(encoded, ct);
        Task<string?> earningsTask = FetchNextEarningsDateAsync(encoded, ct);
        Task<AnalystConsensus?> consensusTask = FetchRecommendationTrendAsync(encoded, ct);

        await Task.WhenAll(metricsTask, targetTask, earningsTask, consensusTask);

        MetricData? m = metricsTask.Result?.Metric;

        return new FinnhubStockMetrics(
            m?.Beta,
            targetTask.Result,
            earningsTask.Result,
            m?.PeTtm,
            m?.PbAnnual,
            m?.RoeTtm,
            m?.DebtToEquityAnnual,
            m?.RevenueGrowthTtmYoy,
            m?.EvToEbitdaTtm,
            consensusTask.Result
        );
    }

    private async Task<MetricResponse?> FetchMetricsAsync(string encodedSymbol, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string url = $"stock/metric?symbol={encodedSymbol}&metric=all&token={_options.ApiKey}";

            HttpResponseMessage response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Finnhub /stock/metric returned {Status} for {Symbol}", response.StatusCode, encodedSymbol);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MetricResponse>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finnhub /stock/metric errored for {Symbol}; returning null.", encodedSymbol);
            return null;
        }
    }

    private async Task<decimal?> FetchPriceTargetAsync(string encodedSymbol, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string url = $"stock/price-target?symbol={encodedSymbol}&token={_options.ApiKey}";

            HttpResponseMessage response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Finnhub /stock/price-target returned {Status} for {Symbol}", response.StatusCode, encodedSymbol);
                return null;
            }

            PriceTargetResponse? body = await response.Content.ReadFromJsonAsync<PriceTargetResponse>(JsonOptions, ct);
            return body?.TargetMean;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finnhub /stock/price-target errored for {Symbol}; returning null.", encodedSymbol);
            return null;
        }
    }

    private async Task<string?> FetchNextEarningsDateAsync(string encodedSymbol, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string from = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string to = DateTime.UtcNow.AddDays(180).ToString("yyyy-MM-dd");
            string url = $"calendar/earnings?symbol={encodedSymbol}&from={from}&to={to}&token={_options.ApiKey}";

            HttpResponseMessage response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Finnhub /calendar/earnings returned {Status} for {Symbol}", response.StatusCode, encodedSymbol);
                return null;
            }

            EarningsCalendarResponse? body = await response.Content.ReadFromJsonAsync<EarningsCalendarResponse>(JsonOptions, ct);
            return body?.EarningsCalendar?.FirstOrDefault()?.Date;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finnhub /calendar/earnings errored for {Symbol}; returning null.", encodedSymbol);
            return null;
        }
    }

    private async Task<AnalystConsensus?> FetchRecommendationTrendAsync(string encodedSymbol, CancellationToken ct)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            string url = $"stock/recommendation?symbol={encodedSymbol}&token={_options.ApiKey}";

            HttpResponseMessage response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Finnhub /stock/recommendation returned {Status} for {Symbol}", response.StatusCode, encodedSymbol);
                return null;
            }

            IReadOnlyList<RecommendationEntry>? entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<RecommendationEntry>>(JsonOptions, ct);

            RecommendationEntry? latest = entries?.OrderByDescending(e => e.Period).FirstOrDefault();
            if (latest is null)
                return null;

            int buy = (latest.StrongBuy ?? 0) + (latest.Buy ?? 0);
            int hold = latest.Hold ?? 0;
            int sell = (latest.Sell ?? 0) + (latest.StrongSell ?? 0);

            return new AnalystConsensus(buy, hold, sell, latest.Period);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finnhub /stock/recommendation errored for {Symbol}; returning null.", encodedSymbol);
            return null;
        }
    }

    private sealed record MetricResponse(
        [property: JsonPropertyName("metric")] MetricData? Metric);

    private sealed record MetricData(
        [property: JsonPropertyName("beta")] decimal? Beta,
        [property: JsonPropertyName("peBasicExclTTM")] decimal? PeTtm,
        [property: JsonPropertyName("pbAnnual")] decimal? PbAnnual,
        [property: JsonPropertyName("roeTTM")] decimal? RoeTtm,
        [property: JsonPropertyName("totalDebt/totalEquityAnnual")] decimal? DebtToEquityAnnual,
        [property: JsonPropertyName("revenueGrowthTTMYoy")] decimal? RevenueGrowthTtmYoy,
        [property: JsonPropertyName("evToEbitdaTTM")] decimal? EvToEbitdaTtm);

    private sealed record PriceTargetResponse(
        [property: JsonPropertyName("targetMean")] decimal? TargetMean);

    private sealed record EarningsCalendarResponse(
        [property: JsonPropertyName("earningsCalendar")] IReadOnlyList<EarningsEntry>? EarningsCalendar);

    private sealed record EarningsEntry(
        [property: JsonPropertyName("date")] string? Date);

    private sealed record RecommendationEntry(
        [property: JsonPropertyName("period")] string? Period,
        [property: JsonPropertyName("strongBuy")] int? StrongBuy,
        [property: JsonPropertyName("buy")] int? Buy,
        [property: JsonPropertyName("hold")] int? Hold,
        [property: JsonPropertyName("sell")] int? Sell,
        [property: JsonPropertyName("strongSell")] int? StrongSell);
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.ConfigurableObjects;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Infrastructure.Implementations.Gemini;

public sealed class GeminiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<GeminiOptions> options,
    ILogger<GeminiClient> logger
) : IGeminiClient
{
    public const string HttpClientName = "gemini";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GeminiOptions _options = options.Value;

    public async Task<GeminiBrokerFeeResult?> FetchBrokerFeesAsync(Broker broker, string market, CancellationToken ct = default)
    {
        string prompt =
            $"Search the web for the current trading commission/fee schedule of broker {broker} for the {market} market, " +
            "including any FX/currency-conversion fee charged when the traded instrument's currency differs from the account currency. " +
            "Return ONLY raw JSON (no markdown) with this shape: " +
            "{\"percent\": number|null (percent per trade), \"min_fee\": number|null, \"fixed_fee\": number|null, " +
            "\"fx_conversion_percent\": number|null (percent charged on currency conversion), " +
            "\"currency\": string|null, \"notes\": string|null, \"source_url\": string|null}.";

        BrokerFeePayload? payload = await GenerateJsonAsync<BrokerFeePayload>(prompt, ct);
        if (payload is null)
            return null;

        return new GeminiBrokerFeeResult(
            payload.Percent, payload.MinFee, payload.FixedFee, payload.FxConversionPercent, payload.Currency, payload.Notes, payload.SourceUrl);
    }

    public async Task<GeminiDividendInfoResult?> FetchDividendInfoAsync(string symbol, CancellationToken ct = default)
    {
        string prompt =
            $"Search the web for the latest dividend information for the stock {symbol}. " +
            "Return ONLY raw JSON (no markdown) with this shape: " +
            "{\"dividend_yield\": number|null (percent), \"forward_dividend\": number|null (annual amount per share), " +
            "\"ex_dividend_date\": string|null (YYYY-MM-DD), " +
            "\"pay_frequency\": one of MONTHLY|QUARTERLY|SEMI_ANNUAL|ANNUAL|IRREGULAR or null, " +
            "\"hist_growth_cagr\": number|null (percent, dividend CAGR over the last 3-5 years), " +
            "\"source_url\": string|null}.";

        DividendPayload? payload = await GenerateJsonAsync<DividendPayload>(prompt, ct);

        if (payload is null)
            return null;

        DateOnly? exDate = null;

        if (!string.IsNullOrWhiteSpace(payload.ExDividendDate) && DateOnly.TryParse(payload.ExDividendDate, CultureInfo.InvariantCulture, out DateOnly parsed))
            exDate = parsed;

        return new GeminiDividendInfoResult(
            payload.DividendYield, payload.ForwardDividend, exDate,
            NormalizePayFrequency(payload.PayFrequency), payload.HistGrowthCagr, payload.SourceUrl
        );
    }

    public async Task<GeminiReceiptResult?> ParseReceiptAsync(byte[] image, string mimeType, CancellationToken ct = default)
    {
        if (image.Length == 0)
            return null;

        const string prompt =
            "You are a receipt/invoice parser. Read the attached image and extract the purchase. " +
            "Return ONLY raw JSON (no markdown) with this shape: " +
            "{\"merchant\": string|null (store or biller name), \"total\": number|null (grand total actually paid), " +
            "\"date\": string|null (YYYY-MM-DD), " +
            "\"category\": string|null (a short spending category such as Food, Utilities, Cigarettes, Subscription, Loan, or Other), " +
            "\"items\": [{\"name\": string, \"qty\": number, \"unit_price\": number, \"amount\": number}] or []}.";

        object payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inlineData = new { mimeType, data = Convert.ToBase64String(image) } },
                        new { text = prompt },
                    },
                },
            },
            generationConfig = new { responseMimeType = "application/json" },
        };

        ReceiptPayload? parsed = await PostGenerateContentAsync<ReceiptPayload>(payload, ct);
        if (parsed is null)
            return null;

        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(parsed.Date)
            && DateOnly.TryParse(parsed.Date, CultureInfo.InvariantCulture, out DateOnly parsedDate))
            date = parsedDate;

        IReadOnlyList<GeminiReceiptItem>? items = parsed.Items?.Count > 0
            ? parsed.Items.Select(i => new GeminiReceiptItem(i.Name, i.Qty, i.UnitPrice, i.Amount)).ToList()
            : null;

        return new GeminiReceiptResult(parsed.Merchant, parsed.Total, date, parsed.Category, items);
    }

    public async Task<GeminiVerdictResult?> SynthesizeVerdictAsync(
        string symbol,
        IReadOnlyList<AlgoScore> scores,
        string? compositeLabel,
        CancellationToken ct = default)
    {
        var algoLines = new StringBuilder();
        foreach (AlgoScore s in scores)
        {
            string context = ExtractAlgoContext(s.Details);
            string detail = context.Length > 0 ? " — " + context : string.Empty;
            algoLines.AppendLine($"- {s.Name}: {s.Score ?? "N/A"}{detail}");
        }

        string prompt = $$"""
            You are a quantitative financial analyst. Given the algorithmic analysis for {{symbol}}, provide a concise investment verdict.

            Algorithmic scores:
            {{algoLines}}
            Composite label: {{compositeLabel ?? "N/A"}}

            Return ONLY valid JSON in this exact shape:
            {
              "verdict": "buy" | "hold" | "risky" | "avoid",
              "summary": "1-2 sentence human-readable synthesis of the quantitative signals",
              "riskWorthIt": true | false,
              "probabilisticWin": null or integer 0-100,
              "coloring": "green" | "yellow" | "red"
            }

            Coloring logic: "green" if verdict is "buy", "red" if verdict is "avoid", "yellow" otherwise.
            Base your verdict strictly on the quantitative signals provided. Be concise. Do not hallucinate data not provided.
            """;

        VerdictPayload? payload = await GenerateStructuredJsonAsync<VerdictPayload>(prompt, ct);
        if (payload is null)
            return null;

        string verdict = payload.Verdict?.ToLowerInvariant() is "buy" or "hold" or "risky" or "avoid"
            ? payload.Verdict.ToLowerInvariant()
            : "hold";

        string coloring = verdict switch
        {
            "buy" => "green",
            "avoid" => "red",
            _ => "yellow",
        };

        if (payload.Coloring?.ToLowerInvariant() is "green" or "yellow" or "red")
            coloring = payload.Coloring.ToLowerInvariant();

        return new GeminiVerdictResult(verdict, payload.Summary ?? string.Empty, payload.RiskWorthIt, payload.ProbabilisticWin, coloring);
    }

    private static string ExtractAlgoContext(IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null)
            return string.Empty;

        foreach (string key in new[] { "interpretation", "zone", "flag", "margin_of_safety" })
        {
            if (details.TryGetValue(key, out object? val) && val is JsonElement el)
            {
                string text = el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? string.Empty
                    : el.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return string.Empty;
    }

    private Task<T?> GenerateStructuredJsonAsync<T>(string prompt, CancellationToken ct) where T : class
    {
        object payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json" },
        };

        return PostGenerateContentAsync<T>(payload, ct);
    }

    private static string? NormalizePayFrequency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string upper = value.Trim().ToUpperInvariant();
        return upper is "MONTHLY" or "QUARTERLY" or "SEMI_ANNUAL" or "ANNUAL" or "IRREGULAR" ? upper : null;
    }

    private Task<T?> GenerateJsonAsync<T>(string prompt, CancellationToken ct) where T : class
    {
        object payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            tools = new[] { new { google_search = new { } } },
        };

        return PostGenerateContentAsync<T>(payload, ct);
    }

    private async Task<T?> PostGenerateContentAsync<T>(object payload, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogWarning("Gemini API key not configured; skipping call.");
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            HttpClient client = httpClientFactory.CreateClient(HttpClientName);

            HttpResponseMessage response = await client.PostAsJsonAsync($"models/{_options.Model}:generateContent", payload, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini call failed with status {Status}", response.StatusCode);
            
                return null;
            }

            GeminiGenerateResponse? body = await response.Content.ReadFromJsonAsync<GeminiGenerateResponse>(JsonOptions, cts.Token);
            
            string? content = body?.Candidates?.FirstOrDefault()?.Content?.Parts is { } parts
                ? string.Concat(parts.Select(p => p.Text))
                : null;

            if (string.IsNullOrWhiteSpace(content))
                return null;

            return JsonSerializer.Deserialize<T>(StripJsonFences(content), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gemini call errored; returning null (best-effort).");
            return null;
        }
    }

    private static string StripJsonFences(string raw)
    {
        string clean = raw.Trim();

        if (clean.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            clean = clean[7..];
        else if (clean.StartsWith("```", StringComparison.Ordinal))
            clean = clean[3..];

        if (clean.EndsWith("```", StringComparison.Ordinal))
            clean = clean[..^3];

        return clean.Trim();
    }

    private sealed record GeminiGenerateResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart>? Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record BrokerFeePayload(
        [property: JsonPropertyName("percent")] decimal? Percent,
        [property: JsonPropertyName("min_fee")] decimal? MinFee,
        [property: JsonPropertyName("fixed_fee")] decimal? FixedFee,
        [property: JsonPropertyName("fx_conversion_percent")] decimal? FxConversionPercent,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("source_url")] string? SourceUrl);

    private sealed record DividendPayload(
        [property: JsonPropertyName("dividend_yield")] decimal? DividendYield,
        [property: JsonPropertyName("forward_dividend")] decimal? ForwardDividend,
        [property: JsonPropertyName("ex_dividend_date")] string? ExDividendDate,
        [property: JsonPropertyName("pay_frequency")] string? PayFrequency,
        [property: JsonPropertyName("hist_growth_cagr")] decimal? HistGrowthCagr,
        [property: JsonPropertyName("source_url")] string? SourceUrl);

    private sealed record ReceiptPayload(
        [property: JsonPropertyName("merchant")] string? Merchant,
        [property: JsonPropertyName("total")] decimal? Total,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("items")] IReadOnlyList<ReceiptItemPayload>? Items);

    private sealed record ReceiptItemPayload(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("qty")] int? Qty,
        [property: JsonPropertyName("unit_price")] decimal? UnitPrice,
        [property: JsonPropertyName("amount")] decimal? Amount);

    private sealed record VerdictPayload(
        [property: JsonPropertyName("verdict")] string? Verdict,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("riskWorthIt")] bool RiskWorthIt,
        [property: JsonPropertyName("probabilisticWin")] int? ProbabilisticWin,
        [property: JsonPropertyName("coloring")] string? Coloring);
}

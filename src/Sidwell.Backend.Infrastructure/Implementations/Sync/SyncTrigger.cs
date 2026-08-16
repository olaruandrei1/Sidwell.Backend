using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.Infrastructure.Implementations.Sync;

public sealed class SyncTrigger(
    IHttpClientFactory httpClientFactory,
    ILogger<SyncTrigger> logger
) : ISyncTrigger
{
    public const string HttpClientName = "sync-trigger";

    public Task TriggerAsync(string symbol, CancellationToken ct = default)
    {
        // Fire-and-forget on a detached task so the caller (add-to-watchlist) returns immediately.
        _ = Task.Run(async () =>
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            if (client.BaseAddress is null)
                return;

            try
            {
                await client.PostAsync($"sync/full/{Uri.EscapeDataString(symbol)}", null);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sync trigger sync/full/{Symbol} failed (best-effort)", symbol);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<bool> TriggerPricesSyncAndWaitAsync(string symbol, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return false;

        try
        {
            HttpResponseMessage response = await client.PostAsync($"sync/prices/{Uri.EscapeDataString(symbol)}", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sync trigger sync/prices/{Symbol} failed", symbol);
            return false;
        }
    }

    public async Task<int> DiscoverUsAsync(CancellationToken ct = default)
    {
        return await PostDiscoverAsync("sync/discover/us", null, ct);
    }

    public async Task<int> DiscoverEuAsync(IReadOnlyList<string> exchanges, CancellationToken ct = default)
    {
        return await PostDiscoverAsync("sync/discover/eu", new { exchanges }, ct);
    }

    public async Task<int> DiscoverBvbAsync(CancellationToken ct = default)
    {
        return await PostDiscoverAsync("sync/discover/bvb", null, ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private async Task<int> PostDiscoverAsync(string path, object? body, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return 0;

        HttpResponseMessage response = body is not null
            ? await client.PostAsJsonAsync(path, body, JsonOpts, ct)
            : await client.PostAsync(path, null, ct);

        response.EnsureSuccessStatusCode();

        using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("upserted", out JsonElement val) ? val.GetInt32() : 0;
    }
}

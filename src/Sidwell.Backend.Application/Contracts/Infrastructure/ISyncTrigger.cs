namespace Sidwell.Backend.Application.Contracts.Infrastructure;

// Fire-and-forget: asks Sync to fetch data for a newly added ticker (profile + prices + news).
// Returns immediately; the sync runs in the background. Never throws to the caller.
public interface ISyncTrigger
{
    Task TriggerAsync(string symbol, CancellationToken ct = default);

    // Awaits a price sync for `symbol` and returns true on HTTP success.
    // Used when AUTO price is requested but price_history has no rows for the ticker.
    Task<bool> TriggerPricesSyncAndWaitAsync(string symbol, CancellationToken ct = default);

    Task<int> DiscoverUsAsync(CancellationToken ct = default);
    Task<int> DiscoverEuAsync(IReadOnlyList<string> exchanges, CancellationToken ct = default);
    Task<int> DiscoverBvbAsync(CancellationToken ct = default);
}

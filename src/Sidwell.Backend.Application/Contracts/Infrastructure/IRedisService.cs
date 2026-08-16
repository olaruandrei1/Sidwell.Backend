namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IRedisService
{
    Task<bool> SetContainsAsync(string key, string member, CancellationToken ct = default);

    Task SetAddAsync(string key, string member, CancellationToken ct = default);

    Task SetRemoveAsync(string key, string member, CancellationToken ct = default);

    Task<IReadOnlyList<string>> SetMembersAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, TimeSpan? expiry = null, CancellationToken ct = default);

    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    Task<bool> KeyExistsAsync(string key, CancellationToken ct = default);
}

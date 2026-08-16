using Sidwell.Backend.Application.Contracts.Infrastructure;
using StackExchange.Redis;

namespace Sidwell.Backend.Infrastructure.Implementations.Redis;

public sealed class RedisService(IConnectionMultiplexer redis) : IRedisService
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<bool> SetContainsAsync(string key, string member, CancellationToken ct = default) =>
        await Db.SetContainsAsync(key, member);

    public async Task SetAddAsync(string key, string member, CancellationToken ct = default) =>
        await Db.SetAddAsync(key, member);

    public async Task SetRemoveAsync(string key, string member, CancellationToken ct = default) =>
        await Db.SetRemoveAsync(key, member);

    public async Task<IReadOnlyList<string>> SetMembersAsync(string key, CancellationToken ct = default)
    {
        RedisValue[] members = await Db.SetMembersAsync(key);
        return members.Select(m => m.ToString()).ToList();
    }

    public async Task SetAsync(string key, string value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (expiry.HasValue)
            await Db.StringSetAsync(key, value, expiry.Value);
        else
            await Db.StringSetAsync(key, value);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        RedisValue value = await Db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await Db.KeyDeleteAsync(key);

    public async Task<bool> KeyExistsAsync(string key, CancellationToken ct = default) =>
        await Db.KeyExistsAsync(key);
}

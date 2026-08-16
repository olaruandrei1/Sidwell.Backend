using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.WebPush;

public sealed class WebPushService(IRedisService redis, IOptions<WebPushOptions> options) : IWebPushService
{
    private readonly WebPushOptions _options = options.Value;

    public string GetPublicKey() => _options.PublicKey;

    public Task SubscribeAsync(Guid userId, string subscriptionJson, CancellationToken ct = default) =>
        redis.SetAsync(SubscriptionKey(userId), subscriptionJson, ct: ct);

    private static string SubscriptionKey(Guid userId) => $"sidwell:push:{userId}";
}

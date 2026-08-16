namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IWebPushService
{
    string GetPublicKey();

    Task SubscribeAsync(Guid userId, string subscriptionJson, CancellationToken ct = default);
}

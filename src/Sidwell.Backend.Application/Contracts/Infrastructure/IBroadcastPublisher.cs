namespace Sidwell.Backend.Application.Contracts.Infrastructure;

// Fire-and-forget publisher to Sidwell.Broadcasting's internal ingest endpoint. Never throws to the caller.
public interface IBroadcastPublisher
{
    Task PublishAsync(string eventName, Guid? userId, object payload, CancellationToken ct = default);
}

using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record DividendLookupJob(string Symbol, Guid TickerId, Guid? RequestedByUserId);

public sealed record BrokerFeeLookupJob(Broker Broker, string Market, Guid? RequestedByUserId);

public interface ILookupQueue
{
    bool TryEnqueueDividend(DividendLookupJob job);

    bool TryEnqueueBrokerFee(BrokerFeeLookupJob job);
}

public sealed record LookupRetryPayload(
    string Kind,
    string? Symbol,
    Guid? TickerId,
    string? Broker,
    string? Market,
    Guid? UserId
);

public static class LookupKeys
{
    public const string DividendKind = "dividend";
    public const string BrokerFeeKind = "brokerfee";
    public const string RetryKeyPrefix = "sidwell:retry:";

    public static string RetryKey(Guid jobId) => $"{RetryKeyPrefix}{jobId}";
}

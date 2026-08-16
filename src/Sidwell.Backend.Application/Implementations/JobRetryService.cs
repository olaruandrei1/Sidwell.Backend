using System.Text.Json;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Implementations;

public sealed class JobRetryService(
    IRedisService redis,
    ILookupQueue queue
) : IJobRetryService
{
    public async Task<JobResultDto> RetryAsync(string jobId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(jobId, out Guid id))
            throw new ValidationException("Invalid job id.");

        string? json = await redis.GetAsync(LookupKeys.RetryKey(id), ct);

        if (string.IsNullOrWhiteSpace(json))
            throw new NotFoundException("No retryable job found for that id (it may have expired or already succeeded).");

        LookupRetryPayload? payload = JsonSerializer.Deserialize<LookupRetryPayload>(json);

        if (payload is null)
            throw new NotFoundException("Retry payload could not be read.");

        bool queued = payload.Kind switch
        {
            LookupKeys.DividendKind when payload is { Symbol: { } symbol, TickerId: { } tickerId } =>
                queue.TryEnqueueDividend(new DividendLookupJob(symbol, tickerId, payload.UserId)),
            LookupKeys.BrokerFeeKind when payload is { Broker: { } broker, Market: { } market } =>
                queue.TryEnqueueBrokerFee(new BrokerFeeLookupJob(BrokerExtensions.FromDbString(broker), market, payload.UserId)),
            _ => false,
        };

        if (!queued)
            return new JobResultDto(false, "A lookup for this item is already in progress.");

        await redis.DeleteAsync(LookupKeys.RetryKey(id), ct);

        return new JobResultDto(true, "Retry queued.");
    }
}

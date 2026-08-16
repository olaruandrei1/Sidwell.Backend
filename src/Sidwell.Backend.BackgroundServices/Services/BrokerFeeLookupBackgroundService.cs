using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.BackgroundServices.Channels;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.BackgroundServices.Services;

public sealed class BrokerFeeLookupBackgroundService(
    LookupQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BrokerFeeLookupBackgroundService> logger
) : LookupBackgroundServiceBase<BrokerFeeLookupJob>(queue, scopeFactory, logger)
{
    private const string UpsertSql = """
        INSERT INTO broker_fee_schedules
            (broker, market, percent, min_fee, fixed_fee, fx_conversion_percent, currency, source_url, fetched_at)
        VALUES
            (@Broker, @Market, @Percent, @MinFee, @FixedFee, @FxConversionPercent, @Currency, @SourceUrl, @FetchedAt)
        ON CONFLICT (broker, market) DO UPDATE SET
            percent = EXCLUDED.percent,
            min_fee = EXCLUDED.min_fee,
            fixed_fee = EXCLUDED.fixed_fee,
            fx_conversion_percent = EXCLUDED.fx_conversion_percent,
            currency = EXCLUDED.currency,
            source_url = EXCLUDED.source_url,
            fetched_at = EXCLUDED.fetched_at
        """;

    protected override ChannelReader<BrokerFeeLookupJob> Reader => Queue.BrokerFeeReader;
    protected override string NotificationType => "BROKER_FEE_LOOKUP_FAILED";
    protected override string DedupKey(BrokerFeeLookupJob job) => LookupQueue.BrokerFeeDedupKey(job.Broker.ToString(), job.Market);
    protected override Guid? UserId(BrokerFeeLookupJob job) => job.RequestedByUserId;
    protected override string Describe(BrokerFeeLookupJob job) => $"Broker fee ({job.Broker}/{job.Market})";
    protected override string SuccessEventName => "BROKER_FEE_READY";
    protected override object SuccessPayload(BrokerFeeLookupJob job) => new { broker = job.Broker.ToString(), market = job.Market };

    protected override LookupRetryPayload BuildRetryPayload(BrokerFeeLookupJob job) =>
        new(LookupKeys.BrokerFeeKind, null, null, job.Broker.ToDbString(), job.Market, job.RequestedByUserId);

    protected override async Task ProcessAsync(BrokerFeeLookupJob job, IServiceProvider services, CancellationToken ct)
    {
        var gemini = services.GetRequiredService<IGeminiClient>();
        var uow = services.GetRequiredService<IUnitOfWork>();

        GeminiBrokerFeeResult? result = await gemini.FetchBrokerFeesAsync(job.Broker, job.Market, ct);
        if (result is null)
            throw new InvalidOperationException($"Gemini returned no fee schedule for {job.Broker}/{job.Market}");

        await uow.Dapper.ExecuteAsync(UpsertSql, new
        {
            Broker = job.Broker.ToDbString(),
            job.Market,
            result.Percent,
            result.MinFee,
            result.FixedFee,
            result.FxConversionPercent,
            result.Currency,
            result.SourceUrl,
            FetchedAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}

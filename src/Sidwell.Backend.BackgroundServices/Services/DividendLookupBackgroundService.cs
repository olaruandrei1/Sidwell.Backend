using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.BackgroundServices.Channels;

namespace Sidwell.Backend.BackgroundServices.Services;

public sealed class DividendLookupBackgroundService(
    LookupQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DividendLookupBackgroundService> logger
) : LookupBackgroundServiceBase<DividendLookupJob>(queue, scopeFactory, logger)
{
    private const string UpsertSql = """
        INSERT INTO ticker_dividends
            (ticker_id, dividend_yield, forward_dividend, ex_dividend_date, pay_frequency, hist_growth_cagr, source_url, fetched_at)
        VALUES
            (@TickerId, @DividendYield, @ForwardDividend, @ExDividendDate, @PayFrequency, @HistGrowthCagr, @SourceUrl, @FetchedAt)
        ON CONFLICT (ticker_id) DO UPDATE SET
            dividend_yield = EXCLUDED.dividend_yield,
            forward_dividend = EXCLUDED.forward_dividend,
            ex_dividend_date = EXCLUDED.ex_dividend_date,
            pay_frequency = EXCLUDED.pay_frequency,
            hist_growth_cagr = EXCLUDED.hist_growth_cagr,
            source_url = EXCLUDED.source_url,
            fetched_at = EXCLUDED.fetched_at
        """;

    protected override ChannelReader<DividendLookupJob> Reader => Queue.DividendReader;
    protected override string NotificationType => "DIVIDEND_LOOKUP_FAILED";
    protected override string DedupKey(DividendLookupJob job) => LookupQueue.DividendDedupKey(job.Symbol);
    protected override Guid? UserId(DividendLookupJob job) => job.RequestedByUserId;
    protected override string Describe(DividendLookupJob job) => $"Dividend ({job.Symbol})";
    protected override string SuccessEventName => "DIVIDEND_READY";
    protected override object SuccessPayload(DividendLookupJob job) => new { symbol = job.Symbol };

    protected override LookupRetryPayload BuildRetryPayload(DividendLookupJob job) =>
        new(LookupKeys.DividendKind, job.Symbol, job.TickerId, null, null, job.RequestedByUserId);

    protected override async Task ProcessAsync(DividendLookupJob job, IServiceProvider services, CancellationToken ct)
    {
        var gemini = services.GetRequiredService<IGeminiClient>();
        var uow = services.GetRequiredService<IUnitOfWork>();

        GeminiDividendInfoResult? result = await gemini.FetchDividendInfoAsync(job.Symbol, ct);

        if (result is null)
            throw new InvalidOperationException($"Gemini returned no dividend info for {job.Symbol}");

        await uow.Dapper.ExecuteAsync(UpsertSql, new
        {
            job.TickerId,
            result.DividendYield,
            result.ForwardDividend,
            result.ExDividendDate,
            result.PayFrequency,
            result.HistGrowthCagr,
            result.SourceUrl,
            FetchedAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}

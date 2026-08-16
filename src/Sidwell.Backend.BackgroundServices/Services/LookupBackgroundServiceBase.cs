using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.BackgroundServices.Channels;

namespace Sidwell.Backend.BackgroundServices.Services;

public abstract class LookupBackgroundServiceBase<TJob>(
    LookupQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger logger
) : BackgroundService
{
    private const int MaxAttempts = 3;

    private const string InsertNotificationSql = """
        INSERT INTO notifications (id, user_id, type, title, body, is_read, created_at)
        VALUES (@Id, @UserId, @Type, @Title, @Body, false, @CreatedAt)
        """;

    protected LookupQueue Queue => queue;

    protected abstract System.Threading.Channels.ChannelReader<TJob> Reader { get; }
    protected abstract string NotificationType { get; }
    protected abstract string DedupKey(TJob job);
    protected abstract Guid? UserId(TJob job);
    protected abstract string Describe(TJob job);
    protected abstract Task ProcessAsync(TJob job, IServiceProvider services, CancellationToken ct);
    protected abstract LookupRetryPayload BuildRetryPayload(TJob job);
    protected abstract string SuccessEventName { get; }
    protected abstract object SuccessPayload(TJob job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (TJob job in Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunWithRetryAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Type}: unexpected error processing {Job}", NotificationType, Describe(job));
                queue.Complete(DedupKey(job));
            }
        }
    }

    private async Task RunWithRetryAsync(TJob job, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await queue.GeminiGate.WaitAsync(ct);

            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                await ProcessAsync(job, scope.ServiceProvider, ct);

                if (UserId(job) is { } successUserId)
                    await scope.ServiceProvider.GetRequiredService<IBroadcastPublisher>()
                        .PublishAsync(SuccessEventName, successUserId, SuccessPayload(job), ct);

                queue.Complete(DedupKey(job));

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                queue.Complete(DedupKey(job));

                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Type}: attempt {Attempt}/{Max} failed for {Job}", NotificationType, attempt, MaxAttempts, Describe(job));

                if (attempt == MaxAttempts)
                {
                    await HandleFailureAsync(job, ct);

                    queue.Complete(DedupKey(job));

                    return;
                }
            }
            finally
            {
                queue.GeminiGate.Release();
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }

    private async Task HandleFailureAsync(TJob job, CancellationToken ct)
    {
        Guid jobId = Guid.NewGuid();

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IRedisService redis = scope.ServiceProvider.GetRequiredService<IRedisService>();

            string payload = System.Text.Json.JsonSerializer.Serialize(BuildRetryPayload(job));

            await redis.SetAsync(LookupKeys.RetryKey(jobId), payload, TimeSpan.FromDays(7), ct);

            if (UserId(job) is { } userId)
            {
                IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await uow.Dapper.ExecuteAsync(InsertNotificationSql, new
                {
                    Id = jobId,
                    UserId = userId,
                    Type = NotificationType,
                    Title = $"{Describe(job)} lookup failed",
                    Body = "The automatic lookup failed. Tap retry to try again.",
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);

                await scope.ServiceProvider.GetRequiredService<IBroadcastPublisher>()
                    .PublishAsync("JOB_FAILED", userId, new { type = NotificationType, item = Describe(job) }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Type}: failed to persist failure notification for {Job}", NotificationType, Describe(job));
        }
    }
}

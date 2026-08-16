using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.BackgroundServices.Services;

[DisallowConcurrentExecution]
public sealed class ReceiptCleanupJob(
    IOptions<FinanceOptions> options,
    ILogger<ReceiptCleanupJob> logger
) : IJob
{
    public const string JobKey = "receipt-cleanup";

    private readonly FinanceOptions _options = options.Value;

    public Task Execute(IJobExecutionContext context)
    {
        string root = _options.ReceiptStoragePath;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Task.CompletedTask;

        int retentionDays = _options.ReceiptRetentionDays > 0 ? _options.ReceiptRetentionDays : 65;

        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

        int deleted = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                        continue;

                    File.Delete(file);

                    deleted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete receipt file {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Receipt cleanup enumeration failed for {Root}", root);
        }

        if (deleted > 0)
            logger.LogInformation("Receipt cleanup removed {Count} file(s) older than {Days} days.", deleted, retentionDays);

        return Task.CompletedTask;
    }
}

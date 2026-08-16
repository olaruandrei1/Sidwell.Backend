using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.BackgroundServices.Channels;
using Sidwell.Backend.BackgroundServices.Services;

namespace Sidwell.Backend.BackgroundServices;

public static class DependencyInjection
{
    public static IServiceCollection AddBackendBackgroundServices(this IServiceCollection services)
    {
        services.AddSingleton<LookupQueue>();
        services.AddSingleton<ILookupQueue>(sp => sp.GetRequiredService<LookupQueue>());

        services.AddHostedService<DividendLookupBackgroundService>();
        services.AddHostedService<BrokerFeeLookupBackgroundService>();

        services.AddQuartz(q =>
        {
            var jobKey = new JobKey(ReceiptCleanupJob.JobKey);
            q.AddJob<ReceiptCleanupJob>(o => o.WithIdentity(jobKey));
            q.AddTrigger(t => t
                .ForJob(jobKey)
                .WithIdentity(ReceiptCleanupJob.JobKey + "-trigger")
                .WithCronSchedule("0 30 3 * * ?"));
        });

        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

        return services;
    }
}

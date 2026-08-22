using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;
using Sidwell.Backend.Infrastructure.Implementations.Auth;
using Sidwell.Backend.Infrastructure.Implementations.Broadcast;
using Sidwell.Backend.Infrastructure.Implementations.Finnhub;
using Sidwell.Backend.Infrastructure.Implementations.Gemini;
using Sidwell.Backend.Infrastructure.Implementations.Receipts;
using Sidwell.Backend.Infrastructure.Implementations.Recalc;
using Sidwell.Backend.Infrastructure.Implementations.Reports;
using Sidwell.Backend.Infrastructure.Implementations.Redis;
using Sidwell.Backend.Infrastructure.Implementations.Sync;
using Sidwell.Backend.Infrastructure.Implementations.WebPush;
using Sidwell.Backend.Infrastructure.Implementations.Yfinance;
using StackExchange.Redis;

namespace Sidwell.Backend.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBackendInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FirebaseOptions>(config.GetSection(FirebaseOptions.SectionName));
        services.Configure<GeminiOptions>(config.GetSection(GeminiOptions.SectionName));
        services.Configure<RedisOptions>(config.GetSection(RedisOptions.SectionName));
        services.Configure<InternalServicesOptions>(config.GetSection(InternalServicesOptions.SectionName));
        services.Configure<FinanceOptions>(config.GetSection(FinanceOptions.SectionName));
        services.Configure<WebPushOptions>(config.GetSection(WebPushOptions.SectionName));
        services.Configure<FinnhubOptions>(config.GetSection(FinnhubOptions.SectionName));
        services.Configure<YfinanceOptions>(config.GetSection(YfinanceOptions.SectionName));

        string redisConnection = config.GetSection(RedisOptions.SectionName).Get<RedisOptions>()?.ConnectionString
            ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            ConfigurationOptions options = ConfigurationOptions.Parse(redisConnection);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton<IRedisService, RedisService>();

        services.AddHttpClient();
        services.AddHttpClient(FirebaseTokenValidator.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IFirebaseTokenValidator, FirebaseTokenValidator>();

        services.AddHttpClient(FinnhubMetricsClient.HttpClientName, (sp, c) =>
        {
            FinnhubOptions finnhub = sp.GetRequiredService<IOptions<FinnhubOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(finnhub.BaseUrl))
                c.BaseAddress = new Uri(finnhub.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<IFinnhubMetricsClient, FinnhubMetricsClient>();

        services.AddHttpClient(YfinanceMetricsClient.HttpClientName, (sp, c) =>
        {
            YfinanceOptions yfinance = sp.GetRequiredService<IOptions<YfinanceOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(yfinance.BaseUrl))
                c.BaseAddress = new Uri(yfinance.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<IYfinanceMetricsClient, YfinanceMetricsClient>();

        services.AddHttpClient(GeminiClient.HttpClientName, (sp, c) =>
        {
            GeminiOptions gemini = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            c.BaseAddress = new Uri(gemini.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(gemini.TimeoutSeconds + 5);
            if (!string.IsNullOrWhiteSpace(gemini.ApiKey))
                c.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", gemini.ApiKey);
        });
        services.AddSingleton<IGeminiClient, GeminiClient>();

        services.AddScoped<IReceiptImageProcessor, ReceiptImageProcessor>();
        services.AddSingleton<IJournalReportRenderer, PdfJournalReportRenderer>();
        services.AddSingleton<IJournalReportRenderer, XlsxJournalReportRenderer>();
        services.AddSingleton<IExpenseExportRenderer, PdfExpenseExportRenderer>();
        services.AddSingleton<IExpenseExportRenderer, XlsxExpenseExportRenderer>();
        // JournalReportService picks the right one via IEnumerable<IJournalReportRenderer> + CanRender().
        services.AddSingleton<IWebPushService, WebPushService>();

        services.Configure<BroadcastOptions>(config.GetSection(BroadcastOptions.SectionName));
        services.AddHttpClient(BroadcastPublisher.HttpClientName, (sp, c) =>
        {
            BroadcastOptions broadcast = sp.GetRequiredService<IOptions<BroadcastOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(broadcast.BaseUrl))
                c.BaseAddress = new Uri(broadcast.BaseUrl);
        });
        services.AddScoped<IBroadcastPublisher, BroadcastPublisher>();

        services.AddHttpClient(SyncTrigger.HttpClientName, (sp, c) =>
        {
            InternalServicesOptions internalSvc = sp.GetRequiredService<IOptions<InternalServicesOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(internalSvc.SyncApiBaseUrl))
                c.BaseAddress = new Uri(internalSvc.SyncApiBaseUrl);
        });
        services.AddSingleton<ISyncTrigger, SyncTrigger>();

        services.AddHttpClient(CoreRecalcTrigger.HttpClientName, (sp, c) =>
        {
            InternalServicesOptions internalSvc = sp.GetRequiredService<IOptions<InternalServicesOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(internalSvc.CoreBaseUrl))
                c.BaseAddress = new Uri(internalSvc.CoreBaseUrl);
        });
        services.AddSingleton<ICoreRecalcTrigger, CoreRecalcTrigger>();

        services.AddHttpClient(CoreIndicatorsClient.HttpClientName, (sp, c) =>
        {
            InternalServicesOptions internalSvc = sp.GetRequiredService<IOptions<InternalServicesOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(internalSvc.CoreBaseUrl))
                c.BaseAddress = new Uri(internalSvc.CoreBaseUrl);
        });
        services.AddScoped<ICoreIndicatorsClient, CoreIndicatorsClient>();

        services.AddHttpClient(CoreVerdictClient.HttpClientName, (sp, c) =>
        {
            InternalServicesOptions internalSvc = sp.GetRequiredService<IOptions<InternalServicesOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(internalSvc.CoreBaseUrl))
                c.BaseAddress = new Uri(internalSvc.CoreBaseUrl);
        });
        services.AddScoped<ICoreVerdictClient, CoreVerdictClient>();

        return services;
    }
}

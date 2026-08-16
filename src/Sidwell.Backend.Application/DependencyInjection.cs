using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Implementations;

namespace Sidwell.Backend.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddBackendApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IAlgorithmMetadataService, AlgorithmMetadataService>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITickerDetailService, TickerDetailService>();
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IWatchlistService, WatchlistService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IBrokerFeeService, BrokerFeeService>();
        services.AddScoped<IDividendProjectionService, DividendProjectionService>();
        services.AddScoped<IJobRetryService, JobRetryService>();
        services.AddScoped<IFinanceService, FinanceService>();
        services.AddScoped<IFinanceSimulationService, FinanceSimulationService>();
        services.AddScoped<IScreenerService, ScreenerService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<ITickerNotesService, TickerNotesService>();
        services.AddScoped<IJournalReportService, JournalReportService>();
        services.AddScoped<ITickerIndicatorsService, TickerIndicatorsService>();
        services.AddScoped<ITickerVerdictService, TickerVerdictService>();

        return services;
    }
}

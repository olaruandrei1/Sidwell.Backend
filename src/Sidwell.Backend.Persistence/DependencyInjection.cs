using Microsoft.Extensions.DependencyInjection;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Persistence.Implementations;

namespace Sidwell.Backend.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddBackendPersistence(this IServiceCollection services, string connectionString)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        Dapper.SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        Dapper.SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());

        services.AddSingleton<IDatabaseConnectionFactory>(new DatabaseConnectionFactory(connectionString));

        services.AddScoped<IDapperExecutor, DapperExecutor>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}

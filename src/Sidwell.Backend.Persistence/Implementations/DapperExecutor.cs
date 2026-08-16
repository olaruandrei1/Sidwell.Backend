using Dapper;
using Sidwell.Backend.Application.Contracts.Persistence;
using System.Data;

namespace Sidwell.Backend.Persistence.Implementations;

public sealed class DapperExecutor(IDatabaseConnectionFactory connectionFactory) : IDapperExecutor
{
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        using IDbConnection connection = await connectionFactory.CreateOpenAsync(ct);

        var rows = await connection.QueryAsync<T>(new CommandDefinition(sql, param, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        using IDbConnection connection = await connectionFactory.CreateOpenAsync(ct);

        return await connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, param, cancellationToken: ct));
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        using IDbConnection connection = await connectionFactory.CreateOpenAsync(ct);

        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, param, cancellationToken: ct));
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        using IDbConnection connection = await connectionFactory.CreateOpenAsync(ct);

        return await connection.ExecuteAsync(new CommandDefinition(sql, param, cancellationToken: ct));
    }
}

using System.Data;
using System.Data.Common;
using Npgsql;
using Sidwell.Backend.Application.Contracts.Persistence;

namespace Sidwell.Backend.Persistence.Implementations;

public sealed class DatabaseConnectionFactory(string connectionString) : IDatabaseConnectionFactory
{
    public DbConnection Create() => new NpgsqlConnection(connectionString);

    public async Task<IDbConnection> CreateOpenAsync(CancellationToken ct = default)
    {
        NpgsqlConnection connection = new(connectionString);

        await connection.OpenAsync(ct);

        return connection;
    }
}

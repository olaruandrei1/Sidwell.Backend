namespace Sidwell.Backend.Application.Contracts.Persistence;

public interface IDapperExecutor
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);
}

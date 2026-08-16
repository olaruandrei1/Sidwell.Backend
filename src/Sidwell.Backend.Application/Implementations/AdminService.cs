using System.Globalization;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class AdminService(IUnitOfWork uow, IRedisService redis) : IAdminService
{
    private const string WhitelistKey = "sidwell:whitelist";

    private const string IsAdminSql = "SELECT is_admin FROM users WHERE id = @userId";

    private const string ListUsersSql = """
        SELECT id AS Id, email AS Email, display_name AS DisplayName, is_admin AS IsAdmin, created_at AS CreatedAt
        FROM users
        ORDER BY created_at
        """;

    public Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default) =>
        IsAdminInternalAsync(userId, ct);

    public async Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(Guid actingUserId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actingUserId, ct);

        IReadOnlyList<UserRow> users = await uow.Dapper.QueryAsync<UserRow>(ListUsersSql, ct: ct);
        IReadOnlyList<string> whitelist = await redis.SetMembersAsync(WhitelistKey, ct);

        HashSet<string> whitelisted = new(whitelist, StringComparer.OrdinalIgnoreCase);

        return users.Select(u => new AdminUserDto(
            u.Id.ToString(),
            u.Email,
            u.DisplayName,
            u.IsAdmin,
            whitelisted.Contains(u.Email),
            u.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
        ).ToList();
    }

    public async Task<IReadOnlyList<string>> ListWhitelistAsync(Guid actingUserId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actingUserId, ct);

        return await redis.SetMembersAsync(WhitelistKey, ct);
    }

    public async Task GrantAccessAsync(Guid actingUserId, string email, CancellationToken ct = default)
    {
        await RequireAdminAsync(actingUserId, ct);

        string normalized = Normalize(email);

        if (normalized.Length == 0)
            throw new ValidationException("Email is required.");

        await redis.SetAddAsync(WhitelistKey, normalized, ct);
    }

    public async Task RevokeAccessAsync(Guid actingUserId, string email, CancellationToken ct = default)
    {
        await RequireAdminAsync(actingUserId, ct);

        string normalized = Normalize(email);

        if (normalized.Length == 0)
            throw new ValidationException("Email is required.");

        await redis.SetRemoveAsync(WhitelistKey, normalized, ct);
    }

    private async Task RequireAdminAsync(Guid userId, CancellationToken ct)
    {
        if (!await IsAdminInternalAsync(userId, ct))
            throw new ForbiddenException("Admin access required.");
    }

    private async Task<bool> IsAdminInternalAsync(Guid userId, CancellationToken ct) =>
        await uow.Dapper.ExecuteScalarAsync<bool>(IsAdminSql, new { userId }, ct);

    private static string Normalize(string email) => email.Trim();

    private sealed record UserRow(Guid Id, string Email, string? DisplayName, bool IsAdmin, DateTimeOffset CreatedAt);
}

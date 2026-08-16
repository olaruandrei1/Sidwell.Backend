using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IAdminService
{
    Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(Guid actingUserId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListWhitelistAsync(Guid actingUserId, CancellationToken ct = default);

    Task GrantAccessAsync(Guid actingUserId, string email, CancellationToken ct = default);

    Task RevokeAccessAsync(Guid actingUserId, string email, CancellationToken ct = default);
}

using System.Text.Json;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IAuthService
{
    Task<AuthSessionResult> CreateSessionAsync(string firebaseIdToken, CancellationToken ct = default);

    Task<AuthenticatedUser?> ValidateSessionAsync(string sessionToken, CancellationToken ct = default);

    Task RevokeSessionAsync(string sessionToken, CancellationToken ct = default);

    Task<object> GetPasskeyRegisterOptionsAsync(string userId, string userEmail, CancellationToken ct = default);

    Task<bool> RegisterPasskeyAsync(string userId, JsonElement credentialPayload, CancellationToken ct = default);

    Task<object> GetPasskeyLoginOptionsAsync(string? email, CancellationToken ct = default);

    Task<AuthSessionResult> LoginWithPasskeyAsync(JsonElement credentialPayload, CancellationToken ct = default);
}

public sealed record AuthSessionResult(string Token, UserDto User);

public sealed record AuthenticatedUser(string UserId, string Email);


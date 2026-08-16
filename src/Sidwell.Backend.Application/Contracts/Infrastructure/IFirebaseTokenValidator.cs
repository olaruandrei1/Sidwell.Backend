namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface IFirebaseTokenValidator
{
    Task<FirebaseTokenClaims?> ValidateAsync(string idToken, CancellationToken ct = default);
}

public sealed record FirebaseTokenClaims(
    string Uid,
    string Email,
    bool EmailVerified,
    string? Name
);

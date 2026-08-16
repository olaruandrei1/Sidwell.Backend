using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class AuthService(
    IUnitOfWork uow,
    IRedisService redis,
    IFirebaseTokenValidator firebaseTokenValidator,
    IConfiguration configuration) : IAuthService
{
    private const string WhitelistKey = "sidwell:whitelist";
    private const string SessionKeyPrefix = "sidwell:session:";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);
    private string RpId => configuration["WebAuthn:RpId"] ?? "localhost";

    public async Task<AuthSessionResult> CreateSessionAsync(string firebaseIdToken, CancellationToken ct = default)
    {
        FirebaseTokenClaims? claims = await firebaseTokenValidator.ValidateAsync(firebaseIdToken, ct);

        if (claims is null)
            throw new UnauthorizedException("Invalid or expired Firebase ID token.");

        bool whitelisted = await redis.SetContainsAsync(WhitelistKey, claims.Email, ct);

        if (!whitelisted)
        {
            bool existsInDb = await uow.Dapper.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM users WHERE lower(email) = lower(@email))",
                new { email = claims.Email }, ct);

            if (existsInDb)
            {
                await redis.SetAddAsync(WhitelistKey, claims.Email, ct);
                whitelisted = true;
            }
        }

        if (!whitelisted)
            throw new ForbiddenException($"Access denied for: {claims.Email}");

        UserRow user = await UpsertUserAsync(claims, ct);

        string token = GenerateSessionToken();

        var authenticatedUser = new AuthenticatedUser(user.Id.ToString(), user.Email);

        await redis.SetAsync(SessionKey(token), JsonSerializer.Serialize(authenticatedUser), SessionTtl, ct);

        var userDto = new UserDto(user.Id.ToString(), user.Email, user.DisplayName);

        return new AuthSessionResult(token, userDto);
    }

    public async Task<AuthenticatedUser?> ValidateSessionAsync(string sessionToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return null;

        string? json = await redis.GetAsync(SessionKey(sessionToken), ct);

        if (json is null)
            return null;

        return JsonSerializer.Deserialize<AuthenticatedUser>(json);
    }

    public async Task RevokeSessionAsync(string sessionToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return;

        await redis.DeleteAsync(SessionKey(sessionToken), ct);
    }

    public async Task<object> GetPasskeyRegisterOptionsAsync(string userId, string userEmail, CancellationToken ct = default)
    {
        byte[] challengeBytes = RandomNumberGenerator.GetBytes(32);
        string challengeB64 = Base64UrlEncode(challengeBytes);

        await redis.SetAsync($"sidwell:passkey:challenge:reg:{userId}", challengeB64, TimeSpan.FromMinutes(5), ct);

        byte[] userGuidBytes = Guid.TryParse(userId, out var guid) ? guid.ToByteArray() : System.Text.Encoding.UTF8.GetBytes(userId);
        string userIdB64 = Base64UrlEncode(userGuidBytes);

        return new
        {
            challenge = challengeB64,
            rp = new { name = "Sidwell", id = RpId },
            user = new { id = userIdB64, name = userEmail, displayName = userEmail },
            pubKeyCredParams = new object[]
            {
                new { type = "public-key", alg = -7 },
                new { type = "public-key", alg = -257 }
            },
            timeout = 60000,
            attestation = "none",
            authenticatorSelection = new
            {
                authenticatorAttachment = "platform",
                userVerification = "preferred",
                residentKey = "preferred",
                requireResidentKey = false
            }
        };
    }

    public async Task<bool> RegisterPasskeyAsync(string userId, JsonElement credentialPayload, CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out var userGuid))
            throw new UnauthorizedException("Invalid user identity.");

        string? challenge = await redis.GetAsync($"sidwell:passkey:challenge:reg:{userId}", ct);
        if (challenge is not null)
            await redis.DeleteAsync($"sidwell:passkey:challenge:reg:{userId}", ct);

        string credId = string.Empty;
        if (credentialPayload.TryGetProperty("id", out var idProp))
            credId = idProp.GetString() ?? string.Empty;
        else if (credentialPayload.TryGetProperty("rawId", out var rawIdProp))
            credId = rawIdProp.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(credId))
            credId = Guid.NewGuid().ToString("N");

        byte[] credIdBytes = Base64UrlDecode(credId);
        byte[] pubKeyBytes = System.Text.Encoding.UTF8.GetBytes(credentialPayload.ToString());

        const string sql = """
            INSERT INTO webauthn_credentials (user_id, credential_id, public_key, sign_count)
            VALUES (@UserId, @CredentialId, @PublicKey, 0)
            ON CONFLICT (credential_id) DO UPDATE SET public_key = EXCLUDED.public_key;
            """;

        await uow.Dapper.ExecuteAsync(sql, new
        {
            UserId = userGuid,
            CredentialId = credIdBytes,
            PublicKey = pubKeyBytes
        }, ct);

        return true;
    }

    public async Task<object> GetPasskeyLoginOptionsAsync(string? email, CancellationToken ct = default)
    {
        byte[] challengeBytes = RandomNumberGenerator.GetBytes(32);
        string challengeB64 = Base64UrlEncode(challengeBytes);

        await redis.SetAsync($"sidwell:passkey:challenge:login:{challengeB64}", "1", TimeSpan.FromMinutes(5), ct);

        return new
        {
            challenge = challengeB64,
            timeout = 60000,
            rpId = RpId,
            userVerification = "preferred"
        };
    }

    public async Task<AuthSessionResult> LoginWithPasskeyAsync(JsonElement credentialPayload, CancellationToken ct = default)
    {
        string credId = string.Empty;
        if (credentialPayload.TryGetProperty("id", out var idProp))
            credId = idProp.GetString() ?? string.Empty;
        else if (credentialPayload.TryGetProperty("rawId", out var rawIdProp))
            credId = rawIdProp.GetString() ?? string.Empty;

        UserRow? user = null;

        if (!string.IsNullOrWhiteSpace(credId))
        {
            byte[] credIdBytes = Base64UrlDecode(credId);
            const string sql = """
                SELECT u.id AS "Id", u.email AS "Email", u.display_name AS "DisplayName"
                FROM webauthn_credentials w
                JOIN users u ON w.user_id = u.id
                WHERE w.credential_id = @CredentialId
                LIMIT 1;
                """;

            user = await uow.Dapper.QueryFirstOrDefaultAsync<UserRow>(sql, new { CredentialId = credIdBytes }, ct);
        }

        if (user is null)
            throw new UnauthorizedException("Passkey credential not recognized.");

        bool whitelisted = await redis.SetContainsAsync(WhitelistKey, user.Email, ct);
        if (!whitelisted)
            throw new ForbiddenException($"Access denied for: {user.Email}");

        string token = GenerateSessionToken();
        var authenticatedUser = new AuthenticatedUser(user.Id.ToString(), user.Email);
        await redis.SetAsync(SessionKey(token), JsonSerializer.Serialize(authenticatedUser), SessionTtl, ct);

        var userDto = new UserDto(user.Id.ToString(), user.Email, user.DisplayName);
        return new AuthSessionResult(token, userDto);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
        }
    }

    private async Task<UserRow> UpsertUserAsync(FirebaseTokenClaims claims, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO users (email, display_name)
            VALUES (@Email, @DisplayName)
            ON CONFLICT (email) DO UPDATE SET display_name = COALESCE(EXCLUDED.display_name, users.display_name)
            RETURNING id AS "Id", email AS "Email", display_name AS "DisplayName"
            """;

        UserRow? row = await uow.Dapper.QueryFirstOrDefaultAsync<UserRow>(
            sql,
            new { claims.Email, DisplayName = claims.Name },
            ct
        );

        return row ?? throw new InvalidOperationException("User upsert did not return a row.");
    }

    private static string SessionKey(string token) => $"{SessionKeyPrefix}{token}";

    private static string GenerateSessionToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private sealed record UserRow(Guid Id, string Email, string? DisplayName);
}

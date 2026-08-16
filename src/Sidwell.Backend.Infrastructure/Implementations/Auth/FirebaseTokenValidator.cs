using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.Auth;

public sealed class FirebaseTokenValidator(
    IHttpClientFactory httpClientFactory,
    IOptions<FirebaseOptions> options,
    ILogger<FirebaseTokenValidator> logger
) : IFirebaseTokenValidator
{
    public const string HttpClientName = "firebase-jwks";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, RSA> _cachedKeys = new Dictionary<string, RSA>();
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public async Task<FirebaseTokenClaims?> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        try
        {
            string[] parts = idToken.Split('.');

            if (parts.Length != 3)
                return null;

            using JsonDocument header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));

            byte[] signature = Base64UrlDecode(parts[2]);

            if (!header.RootElement.TryGetProperty("alg", out JsonElement algElement) || algElement.GetString() != "RS256")
                return null;

            if (!header.RootElement.TryGetProperty("kid", out JsonElement kidElement))
                return null;

            string kid = kidElement.GetString() ?? string.Empty;

            if (string.IsNullOrEmpty(kid))
                return null;

            FirebaseOptions settings = options.Value;

            if (string.IsNullOrEmpty(settings.ProjectId))
            {
                logger.LogWarning("Firebase ProjectId is not configured; rejecting token.");
                return null;
            }

            TimeSpan skew = TimeSpan.FromSeconds(settings.ClockSkewSeconds);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            long exp = payload.RootElement.GetProperty("exp").GetInt64();
            long iat = payload.RootElement.GetProperty("iat").GetInt64();

            string? aud = payload.RootElement.TryGetProperty("aud", out JsonElement audEl) ? audEl.GetString() : null;
            string? iss = payload.RootElement.TryGetProperty("iss", out JsonElement issEl) ? issEl.GetString() : null;
            string? sub = payload.RootElement.TryGetProperty("sub", out JsonElement subEl) ? subEl.GetString() : null;

            if (DateTimeOffset.FromUnixTimeSeconds(exp) + skew < now)
                return null;

            if (DateTimeOffset.FromUnixTimeSeconds(iat) - skew > now)
                return null;

            if (aud != settings.ProjectId)
                return null;

            if (iss != $"https://securetoken.google.com/{settings.ProjectId}")
                return null;

            if (string.IsNullOrEmpty(sub))
                return null;

            RSA? publicKey = await GetSigningKeyAsync(kid, ct);

            if (publicKey is null)
                return null;

            byte[] signedData = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

            bool signatureValid = publicKey.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            if (!signatureValid)
                return null;

            string email = payload.RootElement.TryGetProperty("email", out JsonElement emailEl) ? emailEl.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrEmpty(email))
                return null;

            bool emailVerified = payload.RootElement.TryGetProperty("email_verified", out JsonElement verifiedEl) && verifiedEl.GetBoolean();

            string? name = payload.RootElement.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;

            return new FirebaseTokenClaims(sub, email, emailVerified, name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Firebase ID token validation failed.");

            return null;
        }
    }

    private async Task<RSA?> GetSigningKeyAsync(string kid, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cacheExpiresAt && _cachedKeys.TryGetValue(kid, out RSA? cached))
            return cached;

        await _refreshLock.WaitAsync(ct);

        try
        {
            if (DateTimeOffset.UtcNow < _cacheExpiresAt && _cachedKeys.TryGetValue(kid, out RSA? cachedAfterLock))
                return cachedAfterLock;

            HttpClient client = httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client.GetAsync(options.Value.PublicKeysUrl, ct);

            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync(ct);

            Dictionary<string, string> certsByKid = JsonSerializer.Deserialize<Dictionary<string, string>>(body)
                ?? new Dictionary<string, string>();

            Dictionary<string, RSA> keys = new();

            foreach ((string keyId, string pem) in certsByKid)
            {
                using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);

                RSA? rsaPublicKey = certificate.GetRSAPublicKey();

                if (rsaPublicKey is not null)
                    keys[keyId] = RSA.Create(rsaPublicKey.ExportParameters(false));
            }

            _cachedKeys = keys;
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddHours(options.Value.PublicKeysCacheHours);

            return keys.TryGetValue(kid, out RSA? key) ? key : null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');

        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        return Convert.FromBase64String(padded);
    }
}

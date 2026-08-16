using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Application;

namespace Sidwell.Backend.API.Auth;

public sealed class SessionTokenAuthenticationHandler(
    IOptionsMonitor<SessionTokenAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IAuthService authService
) : AuthenticationHandler<SessionTokenAuthenticationOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = ExtractToken();

        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        AuthenticatedUser? user = await authService.ValidateSessionAsync(token, Context.RequestAborted);

        if (user is null)
            return AuthenticateResult.Fail("Invalid or expired session token.");

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId),
            new Claim(ClaimTypes.Email, user.Email)
        ];

        ClaimsIdentity identity = new(claims, Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private string? ExtractToken()
    {
        string header = Request.Headers["Authorization"].ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        return header["Bearer ".Length..].Trim();
    }
}

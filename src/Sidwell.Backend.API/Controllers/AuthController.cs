using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController(IAuthService authService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request, CancellationToken ct)
    {
        AuthSessionResult result = await authService.CreateSessionAsync(request.IdToken, ct);
        return Ok(new { token = result.Token, user = result.User });
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        string? token = ExtractToken();
        if (!string.IsNullOrEmpty(token))
            await authService.RevokeSessionAsync(token, ct);

        return NoContent();
    }

    [HttpGet("passkey/register/options")]
    [Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
    public async Task<IActionResult> GetPasskeyRegisterOptions(CancellationToken ct)
    {
        string userId = OwnershipGuard.RequireUserId(currentUser);
        string email = currentUser.Email ?? "user@sidwell.local";
        object options = await authService.GetPasskeyRegisterOptionsAsync(userId, email, ct);
        return Ok(options);
    }

    [HttpPost("passkey/register")]
    [Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
    public async Task<IActionResult> RegisterPasskey([FromBody] JsonElement credential, CancellationToken ct)
    {
        string userId = OwnershipGuard.RequireUserId(currentUser);
        bool ok = await authService.RegisterPasskeyAsync(userId, credential, ct);
        return Ok(new { ok });
    }

    [HttpGet("passkey/login/options")]
    public async Task<IActionResult> GetPasskeyLoginOptions([FromQuery] string? email, CancellationToken ct)
    {
        object options = await authService.GetPasskeyLoginOptionsAsync(email, ct);
        return Ok(options);
    }

    [HttpPost("passkey/login")]
    public async Task<IActionResult> LoginWithPasskey([FromBody] JsonElement credential, CancellationToken ct)
    {
        AuthSessionResult result = await authService.LoginWithPasskeyAsync(credential, ct);
        return Ok(new { token = result.Token, user = result.User });
    }

    private string? ExtractToken()
    {
        string header = Request.Headers["Authorization"].ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        return header["Bearer ".Length..].Trim();
    }
}

public sealed record CreateSessionRequest(string IdToken);


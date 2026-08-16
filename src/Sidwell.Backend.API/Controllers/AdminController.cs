using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class AdminController(IAdminService adminService, ICurrentUserAccessor currentUser) : ControllerBase
{
    private Guid UserId => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));

    [HttpGet("admin/whoami")]
    public async Task<ActionResult<object>> WhoAmI(CancellationToken ct) =>
        Ok(new { isAdmin = await adminService.IsAdminAsync(UserId, ct) });

    [HttpGet("admin/users")]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> Users(CancellationToken ct) =>
        Ok(await adminService.ListUsersAsync(UserId, ct));

    [HttpGet("admin/whitelist")]
    public async Task<ActionResult<IReadOnlyList<string>>> Whitelist(CancellationToken ct) =>
        Ok(await adminService.ListWhitelistAsync(UserId, ct));

    [HttpPost("admin/access")]
    public async Task<ActionResult<object>> Grant([FromBody] AccessRequest request, CancellationToken ct)
    {
        await adminService.GrantAccessAsync(UserId, request.Email, ct);
        return Ok(new { ok = true });
    }

    [HttpDelete("admin/access/{email}")]
    public async Task<IActionResult> Revoke(string email, CancellationToken ct)
    {
        await adminService.RevokeAccessAsync(UserId, email, ct);
        return NoContent();
    }

    public sealed record AccessRequest(string Email);
}

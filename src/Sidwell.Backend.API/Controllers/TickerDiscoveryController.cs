using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("tickers/discover")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class TickerDiscoveryController(
    ISyncTrigger syncTrigger,
    IAdminService adminService,
    ICurrentUserAccessor currentUser
) : ControllerBase
{
    [HttpPost("us")]
    public async Task<IActionResult> DiscoverUs(CancellationToken ct)
    {
        await RequireAdmin(ct);
        int upserted = await syncTrigger.DiscoverUsAsync(ct);
        return Ok(new { upserted });
    }

    [HttpPost("eu")]
    public async Task<IActionResult> DiscoverEu([FromBody] DiscoverEuRequest request, CancellationToken ct)
    {
        await RequireAdmin(ct);
        int upserted = await syncTrigger.DiscoverEuAsync(request.Exchanges, ct);
        return Ok(new { upserted });
    }

    [HttpPost("bvb")]
    public async Task<IActionResult> DiscoverBvb(CancellationToken ct)
    {
        await RequireAdmin(ct);
        int upserted = await syncTrigger.DiscoverBvbAsync(ct);
        return Ok(new { upserted });
    }

    private async Task RequireAdmin(CancellationToken ct)
    {
        Guid userId = Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
        if (!await adminService.IsAdminAsync(userId, ct))
            throw new UnauthorizedAccessException("Admin access required.");
    }
}

public sealed record DiscoverEuRequest(IReadOnlyList<string> Exchanges);

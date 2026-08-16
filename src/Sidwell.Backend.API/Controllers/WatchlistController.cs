using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("watchlist")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class WatchlistController(IWatchlistService watchlistService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WatchlistRow>>> Get(CancellationToken ct)
    {
        return Ok(await watchlistService.GetAsync(ResolveUserId(), ct));
    }

    [HttpPost]
    public async Task<ActionResult<WatchlistRow>> Add([FromBody] AddWatchlistRequest request, CancellationToken ct)
    {
        return Ok(await watchlistService.AddAsync(ResolveUserId(), request.Symbol, ct));
    }

    [HttpDelete("{symbol}")]
    public async Task<IActionResult> Remove(string symbol, CancellationToken ct)
    {
        await watchlistService.RemoveAsync(ResolveUserId(), symbol, ct);

        return NoContent();
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record AddWatchlistRequest(string Symbol);

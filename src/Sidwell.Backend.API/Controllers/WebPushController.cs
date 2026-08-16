using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Infrastructure;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("webpush")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class WebPushController(IWebPushService webPushService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("vapid-public-key")]
    public ActionResult<VapidPublicKeyResponse> GetVapidPublicKey() =>
        Ok(new VapidPublicKeyResponse(webPushService.GetPublicKey()));

    // TODO(contract): FE not wired yet (phase 6). Body is the browser's PushSubscription.toJSON()
    // shape ({ endpoint, keys: { p256dh, auth }, expirationTime }); stored as-is, no schema enforced.
    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] JsonElement subscription, CancellationToken ct)
    {
        await webPushService.SubscribeAsync(ResolveUserId(), subscription.GetRawText(), ct);

        return NoContent();
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record VapidPublicKeyResponse(string PublicKey);

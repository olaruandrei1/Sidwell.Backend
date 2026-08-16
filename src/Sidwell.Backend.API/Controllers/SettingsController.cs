using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("settings")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class SettingsController(ISettingsService settingsService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct)
    {
        return Ok(await settingsService.GetAsync(ResolveUserId(), ct));
    }

    [HttpPut]
    public async Task<ActionResult<SettingsDto>> Update([FromBody] UpdateSettingsRequest request, CancellationToken ct)
    {
        return Ok(await settingsService.UpdateAsync(
            ResolveUserId(),
            request.Philosophy,
            request.ReferenceCurrency,
            request.TaxCountry,
            request.PreferredBroker,
            request.DividendProjectionEndYear,
            request.DividendReinvestDefault,
            ct));
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record UpdateSettingsRequest(
    string? Philosophy,
    string? ReferenceCurrency,
    string? TaxCountry,
    string? PreferredBroker,
    int? DividendProjectionEndYear,
    bool? DividendReinvestDefault);

using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("screener")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class ScreenerController(IScreenerService screenerService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<ScreenerResultRow>>> Search([FromBody] Dictionary<string, JsonElement>? criteria, CancellationToken ct)
    {
        ScreenerCriteria parsed = new(ToFilters(criteria));

        return Ok(await screenerService.SearchAsync(ResolveUserId(), parsed, ct));
    }

    [HttpGet("presets")]
    public async Task<ActionResult<IReadOnlyList<ScreenerPreset>>> GetPresets(CancellationToken ct)
    {
        return Ok(await screenerService.GetPresetsAsync(ResolveUserId(), ct));
    }

    [HttpPost("presets")]
    public async Task<ActionResult<ScreenerPreset>> CreatePreset([FromBody] CreateScreenerPresetRequest request, CancellationToken ct)
    {
        ScreenerCriteria criteria = new(ToFilters(request.Criteria));

        return Ok(await screenerService.CreatePresetAsync(ResolveUserId(), request.Name, criteria, ct));
    }

    [HttpDelete("presets/{id:guid}")]
    public async Task<IActionResult> DeletePreset(Guid id, CancellationToken ct)
    {
        await screenerService.DeletePresetAsync(ResolveUserId(), id, ct);

        return NoContent();
    }

    private static IReadOnlyDictionary<string, object?>? ToFilters(Dictionary<string, JsonElement>? criteria) =>
        criteria?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record CreateScreenerPresetRequest(string Name, Dictionary<string, JsonElement>? Criteria);

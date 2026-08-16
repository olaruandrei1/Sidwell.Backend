using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("finances/simulations")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class FinanceSimulationController(
    IFinanceSimulationService simulations,
    ICurrentUserAccessor currentUser
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SavedSimulationDto>>> List(CancellationToken ct)
    {
        return Ok(await simulations.ListAsync(ResolveUserId(), ct));
    }

    [HttpPost]
    public async Task<ActionResult<SavedSimulationDto>> Create([FromBody] SaveSimulationRequest request, CancellationToken ct)
    {
        SavedSimulationDto saved = await simulations.CreateAsync(ResolveUserId(), request.Name, request.Config, ct);

        return StatusCode(StatusCodes.Status201Created, saved);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<SavedSimulationDto>> Update(string id, [FromBody] SaveSimulationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid simulationId))
            throw new NotFoundException($"Simulation '{id}' not found.");

        return Ok(await simulations.UpdateAsync(ResolveUserId(), simulationId, request.Name, request.Config, ct));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out Guid simulationId))
            return NoContent();

        await simulations.DeleteAsync(ResolveUserId(), simulationId, ct);

        return NoContent();
    }

    [HttpPost("run")]
    public async Task<ActionResult<SimulationResultDto>> Run([FromBody] RunSimulationRequest request, CancellationToken ct)
    {
        return Ok(await simulations.RunAsync(ResolveUserId(), request.Config, ct));
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record SaveSimulationRequest(string Name, SimulationConfig Config);

public sealed record RunSimulationRequest(SimulationConfig Config);

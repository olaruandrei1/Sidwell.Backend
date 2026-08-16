using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class PortfolioController(
    IPortfolioService portfolioService,
    ITransactionService transactionService,
    ICurrentUserAccessor currentUser
) : ControllerBase
{
    [HttpGet("portfolio")]
    public async Task<ActionResult<PortfolioDto>> Get(CancellationToken ct)
    {
        return Ok(await portfolioService.GetAsync(ResolveUserId(), ct));
    }

    [HttpPost("transactions")]
    public async Task<IActionResult> CreateTransaction([FromBody] TransactionInput input, CancellationToken ct)
    {
        TransactionResultDto result = await transactionService.CreateAsync(ResolveUserId(), input, ct);

        return Ok(result);
    }

    [HttpPut("transactions/{id}")]
    public async Task<IActionResult> UpdateTransaction(Guid id, [FromBody] TransactionInput input, CancellationToken ct)
    {
        TransactionResultDto result = await transactionService.UpdateAsync(ResolveUserId(), id, input, ct);

        return Ok(result);
    }

    [HttpDelete("transactions/{id}")]
    public async Task<IActionResult> DeleteTransaction(Guid id, CancellationToken ct)
    {
        HoldingDto? holding = await transactionService.DeleteAsync(ResolveUserId(), id, ct);

        return Ok(new { holding });
    }

    [HttpDelete("portfolio/positions/{symbol}")]
    public async Task<IActionResult> DeletePosition(string symbol, CancellationToken ct)
    {
        await portfolioService.DeletePositionAsync(ResolveUserId(), symbol, ct);

        return NoContent();
    }

    [HttpPost("portfolio/positions/{symbol}/recalc")]
    public async Task<IActionResult> RecalcPosition(string symbol, CancellationToken ct)
    {
        HoldingDto? holding = await transactionService.RecalcAsync(ResolveUserId(), symbol, ct);

        return Ok(new { holding });
    }

    [HttpPost("portfolio/recalc-all")]
    public async Task<IActionResult> RecalcAll(CancellationToken ct)
    {
        int count = await transactionService.RecalcAllAsync(ResolveUserId(), ct);

        return Ok(new { recomputed = count });
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

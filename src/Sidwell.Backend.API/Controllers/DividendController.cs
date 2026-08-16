using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
public sealed class DividendController(IDividendProjectionService dividendProjectionService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("tickers/{symbol}/dividends")]
    public async Task<ActionResult<DividendInfoDto>> GetDividends(string symbol, CancellationToken ct)
    {
        return Ok(await dividendProjectionService.GetDividendInfoAsync(symbol, ct));
    }

    [HttpPost("tickers/{symbol}/dividends/projection")]
    public async Task<ActionResult<DividendProjectionDto>> Project(string symbol, [FromBody] DividendProjectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Shares) || !decimal.TryParse(request.Shares, out decimal shares))
            shares = 500m;

        return Ok(await dividendProjectionService.ProjectAsync(
            symbol, shares, request.EndYear ?? 2060, request.Reinvest, currentUser.UserId, ct)
        );
    }

    public sealed record DividendProjectionRequest(string? Shares, int? EndYear, bool Reinvest);
}

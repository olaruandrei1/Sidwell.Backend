using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Route("tickers")]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class TickerController(
    ITickerDetailService tickerDetailService,
    ITransactionService transactionService,
    ICurrentUserAccessor currentUser,
    IAlgorithmMetadataService metadataService,
    ISyncTrigger syncTrigger,
    ITickerIndicatorsService indicatorsService,
    ITickerVerdictService verdictService
) : ControllerBase
{
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<TickerSummary>>> Search([FromQuery] string q, CancellationToken ct)
    {
        return Ok(await tickerDetailService.SearchAsync(q ?? string.Empty, ct));
    }

    [HttpGet("{symbol}")]
    public async Task<ActionResult<TickerDetail>> GetBySymbol(string symbol, CancellationToken ct)
    {
        TickerDetail? detail = await tickerDetailService.GetBySymbolAsync(ResolveUserId(), symbol, ct);

        return detail is null ? NotFound(new { error = $"Ticker '{symbol}' not found." }) : Ok(detail);
    }

    [HttpGet("{symbol}/transactions")]
    public async Task<ActionResult<IReadOnlyList<TransactionDto>>> GetTransactions(string symbol, CancellationToken ct)
    {
        return Ok(await transactionService.GetForTickerAsync(ResolveUserId(), symbol, ct));
    }

    [HttpPut("{symbol}/note")]
    public async Task<IActionResult> UpdateNote(string symbol, [FromBody] UpdateNoteRequest request, CancellationToken ct)
    {
        bool updated = await tickerDetailService.UpdateNoteAsync(ResolveUserId(), symbol, request.Body, ct);

        return updated ? Ok(new { ok = true }) : NotFound(new { error = $"Ticker '{symbol}' not found." });
    }

    [HttpGet("{symbol}/news")]
    public async Task<ActionResult<PaginatedResult<NewsItem>>> GetNews(
        string symbol,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        PaginatedResult<NewsItem>? news = await tickerDetailService.GetNewsPaginatedAsync(symbol, page, pageSize, ct);

        return news is null ? NotFound(new { error = $"Ticker '{symbol}' not found." }) : Ok(news);
    }

    [HttpGet("{symbol}/growth-projection")]
    public async Task<IActionResult> GrowthProjection(string symbol, [FromQuery] decimal targetShares = 1, CancellationToken ct = default)
    {
        GrowthProjectionDto? projection = await tickerDetailService.GetGrowthProjectionAsync(symbol, targetShares, ct);

        return projection is null ? NotFound(new { error = $"Ticker '{symbol}' not found." }) : Ok(projection);
    }

    [HttpGet("{symbol}/latest-price")]
    public async Task<ActionResult<TickerLatestPriceDto>> GetLatestPrice(string symbol, CancellationToken ct)
    {
        return Ok(await tickerDetailService.GetLatestPriceAsync(symbol, ct));
    }

    [HttpGet("{symbol}/my-projection")]
    public async Task<IActionResult> MyProjection(string symbol, CancellationToken ct)
    {
        MyProjectionDto? result = await tickerDetailService.GetMyProjectionAsync(ResolveUserId(), symbol, ct);

        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("algorithms/metadata")]
    [AllowAnonymous]
    public ActionResult<IReadOnlyDictionary<string, AlgorithmMetadata>> GetAlgorithmMetadata()
    {
        return Ok(metadataService.GetAll());
    }

    [HttpGet("{symbol}/indicators")]
    public async Task<IActionResult> GetIndicators(string symbol, [FromQuery] string types, CancellationToken ct)
    {
        string[] requested = (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0)
            return BadRequest(new { error = "Query param 'types' is required (comma-separated, e.g. sma20,ema50,rsi14)." });

        return Ok(await indicatorsService.GetIndicatorsAsync(symbol, requested, ct));
    }

    [HttpGet("{symbol}/verdict")]
    public async Task<IActionResult> GetTechnicalVerdict(string symbol, [FromQuery] string types, CancellationToken ct)
    {
        string[] requested = (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0)
            return BadRequest(new { error = "Query param 'types' is required (comma-separated, e.g. sma20,ema50,rsi14)." });

        return Ok(await verdictService.GetVerdictAsync(symbol, requested, ct));
    }

    [HttpPost("{symbol}/sync")]
    public IActionResult TriggerSync(string symbol)
    {
        syncTrigger.TriggerAsync(symbol.ToUpperInvariant());
        return Accepted(new { queued = true, symbol = symbol.ToUpperInvariant() });
    }

    private Guid ResolveUserId() => Guid.Parse(OwnershipGuard.RequireUserId(currentUser));
}

public sealed record UpdateNoteRequest(string Body);

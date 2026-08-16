using System.Text.Json;
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
    IGeminiClient gemini,
    IRedisService redis,
    ISyncTrigger syncTrigger
) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

    [HttpPost("{symbol}/verdict")]
    public async Task<IActionResult> GetVerdict(string symbol, CancellationToken ct)
    {
        TickerDetail? detail = await tickerDetailService.GetBySymbolAsync(ResolveUserId(), symbol, ct);
        if (detail is null)
            return NotFound();

        // Key the cache on the score fingerprint so a fresh recalc (e.g. N/A -> real
        // scores) produces a new verdict instead of serving the stale cached one.
        string fingerprint = BuildVerdictFingerprint(detail);
        string cacheKey = $"sidwell:verdict:{symbol.ToLowerInvariant()}:{fingerprint}";

        string? cached = await redis.GetAsync(cacheKey, ct);
        if (cached is not null)
        {
            GeminiVerdictResult? cachedResult = JsonSerializer.Deserialize<GeminiVerdictResult>(cached, JsonOptions);
            if (cachedResult is not null)
                return Ok(cachedResult);
        }

        string? compositeLabel = detail.Composite?.Label;
        GeminiVerdictResult? verdict = await gemini.SynthesizeVerdictAsync(symbol, detail.Algorithms, compositeLabel, ct);
        if (verdict is null)
            return StatusCode(503);

        await redis.SetAsync(cacheKey, JsonSerializer.Serialize(verdict, JsonOptions), TimeSpan.FromHours(6), ct);

        return Ok(verdict);
    }

    private static string BuildVerdictFingerprint(TickerDetail detail)
    {
        string composite = detail.Composite?.Score ?? "na";
        IEnumerable<string> algoParts = detail.Algorithms
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => $"{a.Name}={a.Score ?? "na"}");
        string raw = $"{composite}|{string.Join(",", algoParts)}";

        byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
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

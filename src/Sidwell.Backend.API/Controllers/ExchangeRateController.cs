using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class ExchangeRateController(
    IUnitOfWork uow,
    IHttpClientFactory httpClientFactory,
    IOptions<InternalServicesOptions> internalServices,
    ILogger<ExchangeRateController> logger
) : ControllerBase
{
    private static readonly string[] DefaultCurrencies = ["EUR", "USD", "GBP", "SEK", "DKK", "NOK"];

    private const string AllRatesSql = """
        SELECT DISTINCT ON (currency)
            currency AS "Currency",
            rate_date AS "RateDate",
            rate_to_ron AS "RateToRon",
            source AS "Source"
        FROM exchange_rates
        ORDER BY currency, rate_date DESC
        """;

    [HttpGet("settings/exchange-rates")]
    public async Task<ActionResult<IReadOnlyList<ExchangeRateDto>>> GetRates(CancellationToken ct)
    {
        IReadOnlyList<RateRow> rows = await uow.Dapper.QueryAsync<RateRow>(AllRatesSql, ct: ct);

        IReadOnlyList<ExchangeRateDto> result = rows
            .Select(r => new ExchangeRateDto(
                r.Currency,
                r.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.RateToRon.ToString("F6", CultureInfo.InvariantCulture),
                r.Source))
            .ToList();

        return Ok(result);
    }

    [HttpPost("settings/exchange-rates/refresh")]
    public async Task<ActionResult<IReadOnlyList<ExchangeRateDto>>> RefreshRates(CancellationToken ct)
    {
        string baseUrl = internalServices.Value.SyncApiBaseUrl;
        Uri syncUri = new(new Uri(baseUrl, UriKind.Absolute), "sync/fx/currencies");

        try
        {
            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                syncUri, new { currencies = DefaultCurrencies }, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("FX refresh returned {StatusCode} from Sync", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger FX refresh on Sync at {SyncUri}", syncUri);
            return StatusCode(502, new { message = "Exchange rate sync failed — check Sync service" });
        }

        return await GetRates(ct);
    }

    private sealed record RateRow(string Currency, DateOnly RateDate, decimal RateToRon, string Source);
}

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
public sealed class DividendTaxController(
    IUnitOfWork uow,
    IHttpClientFactory httpClientFactory,
    IOptions<InternalServicesOptions> internalServices,
    ILogger<DividendTaxController> logger
) : ControllerBase
{
    private const string TaxRatesSql = """
        SELECT country_code AS "CountryCode", rate_percent AS "RatePercent", notes AS "Notes",
               source_url AS "SourceUrl", fetched_at AS "FetchedAt"
        FROM dividend_tax_rates
        ORDER BY country_code
        """;

    [HttpGet("settings/dividend-tax-rates")]
    public async Task<ActionResult<IReadOnlyList<DividendTaxRateDto>>> GetTaxRates(CancellationToken ct)
    {
        IReadOnlyList<TaxRateRow> rows = await uow.Dapper.QueryAsync<TaxRateRow>(TaxRatesSql, ct: ct);

        IReadOnlyList<DividendTaxRateDto> result = rows
            .Select(r => new DividendTaxRateDto(
                r.CountryCode,
                r.RatePercent.ToString(CultureInfo.InvariantCulture),
                r.Notes,
                r.SourceUrl,
                r.FetchedAt?.ToString("O")))
            .ToList();

        return Ok(result);
    }

    [HttpPost("settings/dividend-tax-rates/refresh")]
    public IActionResult RefreshTaxRates()
    {
        string baseUrl = internalServices.Value.SyncApiBaseUrl;
        Uri syncUri = new(new Uri(baseUrl, UriKind.Absolute), "internal/sync/dividend-tax");

        _ = TriggerRefreshAsync(syncUri);

        return Accepted();
    }

    private async Task TriggerRefreshAsync(Uri syncUri)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(syncUri, content: null);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Dividend tax refresh trigger returned {StatusCode} from Sync.Api", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger dividend tax refresh on Sync.Api at {SyncUri}", syncUri);
        }
    }

    private sealed record TaxRateRow(string CountryCode, decimal RatePercent, string? Notes, string? SourceUrl, DateTimeOffset? FetchedAt);
}

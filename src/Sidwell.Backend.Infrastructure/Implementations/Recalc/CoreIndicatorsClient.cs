using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.Recalc;

public sealed class CoreIndicatorsClient(
    IHttpClientFactory httpClientFactory,
    IOptions<InternalServicesOptions> options,
    ILogger<CoreIndicatorsClient> logger
) : ICoreIndicatorsClient
{
    public const string HttpClientName = "core-indicators";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InternalServicesOptions _options = options.Value;

    public async Task<IReadOnlyList<IndicatorSeriesDto>?> GetIndicatorsAsync(
        Guid tickerId, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null || types.Count == 0)
            return null;

        try
        {
            string typesParam = Uri.EscapeDataString(string.Join(',', types));
            using HttpRequestMessage request = new(HttpMethod.Get, $"indicators/{tickerId}?types={typesParam}");
            request.Headers.TryAddWithoutValidation("X-Internal-Secret", _options.Secret);

            HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Core indicators request for {TickerId} failed with {Status}", tickerId, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<IndicatorSeriesDto>>(Json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Core indicators request for ticker {TickerId} failed", tickerId);
            return null;
        }
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.Recalc;

public sealed class CoreVerdictClient(
    IHttpClientFactory httpClientFactory,
    IOptions<InternalServicesOptions> options,
    ILogger<CoreVerdictClient> logger
) : ICoreVerdictClient
{
    public const string HttpClientName = "core-verdict";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InternalServicesOptions _options = options.Value;

    public async Task<TechnicalVerdictDto?> GetVerdictAsync(
        Guid tickerId, double compositeScore, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null || types.Count == 0)
            return null;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"verdict/{tickerId}")
            {
                Content = JsonContent.Create(new { compositeScore, types }, options: Json)
            };
            request.Headers.TryAddWithoutValidation("X-Internal-Secret", _options.Secret);

            HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Core verdict request for {TickerId} failed with {Status}", tickerId, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TechnicalVerdictDto>(Json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Core verdict request for ticker {TickerId} failed", tickerId);
            return null;
        }
    }
}

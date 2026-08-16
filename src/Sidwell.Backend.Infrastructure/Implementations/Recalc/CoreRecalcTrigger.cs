using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidwell.Backend.Application.Contracts.Infrastructure;
using Sidwell.Backend.Domain.ConfigurableObjects;

namespace Sidwell.Backend.Infrastructure.Implementations.Recalc;

public sealed class CoreRecalcTrigger(
    IHttpClientFactory httpClientFactory,
    IOptions<InternalServicesOptions> options,
    ILogger<CoreRecalcTrigger> logger
) : ICoreRecalcTrigger
{
    public const string HttpClientName = "core-recalc";

    private readonly InternalServicesOptions _options = options.Value;

    public async Task<bool> RecalcAsync(Guid tickerId, DateOnly asOf, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return false;

        try
        {
            using HttpRequestMessage request = BuildRequest(tickerId, asOf);
            HttpResponseMessage response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Core recalc trigger for ticker {TickerId} failed", tickerId);
            return false;
        }
    }

    public void FireAndForget(Guid tickerId, DateOnly asOf)
    {
        _ = Task.Run(async () =>
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            if (client.BaseAddress is null)
                return;

            try
            {
                using HttpRequestMessage request = BuildRequest(tickerId, asOf);
                await client.SendAsync(request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Core recalc fire-and-forget for ticker {TickerId} failed", tickerId);
            }
        }, CancellationToken.None);
    }

    private HttpRequestMessage BuildRequest(Guid tickerId, DateOnly asOf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"recalc/{tickerId}?asOf={asOf:yyyy-MM-dd}");
        request.Headers.TryAddWithoutValidation("X-Internal-Secret", _options.Secret);
        return request;
    }
}

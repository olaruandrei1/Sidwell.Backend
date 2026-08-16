namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public interface ICoreRecalcTrigger
{
    Task<bool> RecalcAsync(Guid tickerId, DateOnly asOf, CancellationToken ct = default);

    void FireAndForget(Guid tickerId, DateOnly asOf);
}

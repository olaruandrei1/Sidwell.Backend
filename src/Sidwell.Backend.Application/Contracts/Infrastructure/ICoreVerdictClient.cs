namespace Sidwell.Backend.Application.Contracts.Infrastructure;

public sealed record ReentryEstimateDto(int EstimatedDays, int SampleCount, double TargetPrice, double CurrentDeviationPct);

public sealed record TechnicalVerdictDto(
    double RawScore,
    double ConvictionPct,
    string Action,
    double AgreementPct,
    ReentryEstimateDto? Reentry = null);

public interface ICoreVerdictClient
{
    Task<TechnicalVerdictDto?> GetVerdictAsync(
        Guid tickerId, double compositeScore, IReadOnlyList<string> types, CancellationToken ct = default);
}

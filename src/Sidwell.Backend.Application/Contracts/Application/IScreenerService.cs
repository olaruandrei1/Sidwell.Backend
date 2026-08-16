using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IScreenerService
{
    Task<IReadOnlyList<ScreenerResultRow>> SearchAsync(Guid userId, ScreenerCriteria criteria, CancellationToken ct = default);

    Task<IReadOnlyList<ScreenerPreset>> GetPresetsAsync(Guid userId, CancellationToken ct = default);

    Task<ScreenerPreset> CreatePresetAsync(Guid userId, string name, ScreenerCriteria criteria, CancellationToken ct = default);

    Task DeletePresetAsync(Guid userId, Guid presetId, CancellationToken ct = default);
}

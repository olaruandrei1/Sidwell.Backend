using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IFinanceSimulationService
{
    Task<IReadOnlyList<SavedSimulationDto>> ListAsync(Guid userId, CancellationToken ct = default);

    Task<SavedSimulationDto> CreateAsync(Guid userId, string name, SimulationConfig config, CancellationToken ct = default);

    Task<SavedSimulationDto> UpdateAsync(Guid userId, Guid id, string name, SimulationConfig config, CancellationToken ct = default);

    Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<SimulationResultDto> RunAsync(Guid userId, SimulationConfig config, CancellationToken ct = default);
}

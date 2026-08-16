using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ITickerNotesService
{
    Task<IReadOnlyList<TickerNoteDto>> GetBySymbolAsync(Guid userId, string symbol, CancellationToken ct = default);
    Task<TickerNoteDto> CreateAsync(Guid userId, string symbol, UpsertTickerNoteRequest request, CancellationToken ct = default);
    Task<TickerNoteDto> UpdateAsync(Guid userId, Guid noteId, UpsertTickerNoteRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid noteId, CancellationToken ct = default);
}

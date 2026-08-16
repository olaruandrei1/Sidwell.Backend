using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface ITransactionService
{
    Task<TransactionResultDto> CreateAsync(Guid userId, TransactionInput input, CancellationToken ct = default);

    Task<TransactionResultDto> UpdateAsync(Guid userId, Guid transactionId, TransactionInput input, CancellationToken ct = default);

    Task<HoldingDto?> DeleteAsync(Guid userId, Guid transactionId, CancellationToken ct = default);

    Task<IReadOnlyList<TransactionDto>> GetForTickerAsync(Guid userId, string symbol, CancellationToken ct = default);

    Task<HoldingDto?> RecalcAsync(Guid userId, string symbol, CancellationToken ct = default);

    Task<int> RecalcAllAsync(Guid userId, CancellationToken ct = default);
}

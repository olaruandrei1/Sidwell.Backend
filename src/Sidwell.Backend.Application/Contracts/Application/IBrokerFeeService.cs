using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IBrokerFeeService
{
    Task<IReadOnlyList<BrokerDto>> GetBrokersAsync(CancellationToken ct = default);

    Task<BrokerFeeEstimate> EstimateFeeAsync(Broker broker, string symbol, decimal shares, decimal price, string currency, Guid userId, CancellationToken ct = default);
}

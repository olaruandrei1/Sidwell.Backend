using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;
using Sidwell.Backend.Domain.Enums;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
public sealed class BrokerController(IBrokerFeeService brokerFeeService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("brokers")]
    public async Task<ActionResult<IReadOnlyList<BrokerDto>>> GetBrokers(CancellationToken ct)
    {
        return Ok(await brokerFeeService.GetBrokersAsync(ct));
    }

    [Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
    [HttpPost("brokers/{broker}/estimate-fee")]
    public async Task<ActionResult<BrokerFeeEstimate>> EstimateFee(string broker, [FromBody] EstimateFeeRequest request, CancellationToken ct)
    {
        Broker parsedBroker;

        try
        {
            parsedBroker = BrokerExtensions.FromDbString(broker.ToUpperInvariant());
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ValidationException($"Unknown broker '{broker}'.");
        }

        if (!decimal.TryParse(request.Shares, out decimal shares) || !decimal.TryParse(request.Price, out decimal price))
            throw new ValidationException("Invalid shares or price.");

        if (string.IsNullOrWhiteSpace(request.Currency))
            throw new ValidationException("Currency is required.");

        Guid userId = Guid.Parse(OwnershipGuard.RequireUserId(currentUser));

        return Ok(await brokerFeeService.EstimateFeeAsync(parsedBroker, request.Symbol, shares, price, request.Currency, userId, ct));
    }

    public sealed record EstimateFeeRequest(string Symbol, string Shares, string Price, string Currency);
}

namespace Sidwell.Backend.Application.Dtos;

public sealed record BrokerNetInvestedDto(
    string Broker,
    string Currency,
    string Amount
);

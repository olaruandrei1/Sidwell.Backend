namespace Sidwell.Backend.Application.Dtos;

public record BrokerFeeEstimate(
    string Fee,
    string BaseFee,
    string FxConversionFee,
    string? Currency,
    bool Estimated,
    string FetchedAt
);

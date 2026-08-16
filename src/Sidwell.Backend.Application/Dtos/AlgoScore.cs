namespace Sidwell.Backend.Application.Dtos;

public record AlgoScore(
    string Name,
    string? Score,
    bool Applicable,
    IReadOnlyDictionary<string, object?>? Details
);

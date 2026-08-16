namespace Sidwell.Backend.Application.Dtos;

public record CompositeScore(
    string Philosophy,
    string Score,
    string Label,
    string Color,
    bool Overridden
);

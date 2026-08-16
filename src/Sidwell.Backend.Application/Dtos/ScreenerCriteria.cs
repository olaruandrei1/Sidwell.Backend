namespace Sidwell.Backend.Application.Dtos;

public record ScreenerCriteria(
    IReadOnlyDictionary<string, object?>? Filters
);

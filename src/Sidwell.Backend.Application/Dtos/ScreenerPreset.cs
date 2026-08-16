namespace Sidwell.Backend.Application.Dtos;

public record ScreenerPreset(
    string Id,
    string Name,
    ScreenerCriteria Criteria
);

namespace Sidwell.Backend.Application.Dtos;

public record UserDto(
    string Id,
    string Email,
    string? DisplayName
);

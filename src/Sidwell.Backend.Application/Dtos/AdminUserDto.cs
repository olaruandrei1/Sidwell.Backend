namespace Sidwell.Backend.Application.Dtos;

public record AdminUserDto(
    string Id,
    string Email,
    string? DisplayName,
    bool IsAdmin,
    bool Whitelisted,
    string CreatedAt
);

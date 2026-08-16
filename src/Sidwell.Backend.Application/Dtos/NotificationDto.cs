namespace Sidwell.Backend.Application.Dtos;

public record NotificationDto(
    string Id,
    string Type,
    string Title,
    string? Body,
    bool IsRead,
    string CreatedAt
);

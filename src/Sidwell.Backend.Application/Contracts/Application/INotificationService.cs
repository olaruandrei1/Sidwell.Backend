using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly, CancellationToken ct = default);

    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default);
}

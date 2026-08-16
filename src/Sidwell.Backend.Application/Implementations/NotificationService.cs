using Sidwell.Backend.Application.Common;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Contracts.Persistence;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Implementations;

public sealed class NotificationService(IUnitOfWork uow) : INotificationService
{
    private const string ListSql = """
        SELECT id AS Id, type AS Type, title AS Title, body AS Body, is_read AS IsRead, created_at AS CreatedAt
        FROM notifications
        WHERE user_id = @userId AND (@unreadOnly = false OR is_read = false)
        ORDER BY created_at DESC;
        """;

    private const string MarkReadSql = "UPDATE notifications SET is_read = true WHERE id = @id AND user_id = @userId;";

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly, CancellationToken ct = default)
    {
        IReadOnlyList<Row> rows = await uow.Dapper.QueryAsync<Row>(ListSql, new { userId, unreadOnly }, ct: ct);

        return rows
            .Select(r => new NotificationDto(r.Id.ToString(), r.Type, r.Title, r.Body, r.IsRead, r.CreatedAt.ToString("O")))
            .ToList();
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        int affected = await uow.Dapper.ExecuteAsync(MarkReadSql, new { id = notificationId, userId }, ct: ct);

        if (affected == 0)
            throw new NotFoundException($"Notification '{notificationId}' not found.");
    }

    private sealed record Row(Guid Id, string Type, string Title, string? Body, bool IsRead, DateTimeOffset CreatedAt);
}

namespace Sidwell.Backend.Application.Common;

public interface ICurrentUserAccessor
{
    bool IsAuthenticated { get; }

    string UserId { get; }

    string? Email { get; }
}

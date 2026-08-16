namespace Sidwell.Backend.Application.Common;

public static class OwnershipGuard
{
    public static string RequireUserId(ICurrentUserAccessor currentUser)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new UnauthorizedException("No authenticated user in the current request.");

        return currentUser.UserId;
    }

    public static void EnsureOwned(string resourceUserId, string currentUserId)
    {
        if (!string.Equals(resourceUserId, currentUserId, StringComparison.Ordinal))
            throw new NotFoundException("Resource not found.");
    }
}

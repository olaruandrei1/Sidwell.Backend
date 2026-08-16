namespace Sidwell.Backend.Application.Common;

public abstract class AppException(string message) : Exception(message);

public sealed class UnauthorizedException(string message) : AppException(message);

public sealed class ForbiddenException(string message) : AppException(message);

public sealed class NotFoundException(string message) : AppException(message);

public sealed class ValidationException(string message) : AppException(message);

public sealed class ConflictException(string message) : AppException(message);

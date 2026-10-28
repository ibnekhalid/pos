using FluentValidation.Results;

namespace Restaurant.Application.Common.Exceptions;

// Mapped to HTTP status codes by the API's exception middleware. Messages are shown to users,
// so they must never contain internal details.

/// <summary>404. Also what another tenant's data looks like: it simply isn't there.</summary>
public class NotFoundException(string entityName) : Exception($"{entityName} not found.");

/// <summary>403.</summary>
public class ForbiddenAccessException(string message = "You don't have permission to do this.") : Exception(message);

/// <summary>409.</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>401.</summary>
public class AuthenticationFailedException(string message = "Incorrect sign-in details.") : Exception(message);

/// <summary>
/// A write touched another tenant's row. Always a bug or an attack — the request is rejected (403)
/// and logged as an error.
/// </summary>
public class TenantIsolationException(string message) : Exception(message);

/// <summary>400 with a field → messages map.</summary>
public class ValidationAppException : Exception
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationAppException(IEnumerable<ValidationFailure> failures)
        : base("One or more validation failures have occurred.")
    {
        Errors = failures
            .GroupBy(f => f.PropertyName, f => f.ErrorMessage)
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    public ValidationAppException(string field, string message)
        : this([new ValidationFailure(field, message)])
    {
    }
}

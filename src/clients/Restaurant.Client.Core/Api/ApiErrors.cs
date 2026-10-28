namespace Restaurant.Client.Core.Api;

/// <summary>The server answered with an error (4xx/5xx). Its message is safe to show to the user.</summary>
public class ApiException(int status, string? title, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(FirstMessage(title, errors))
{
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    /// <summary>First validation message for a field (case-insensitive), e.g. "Email".</summary>
    public string? FieldError(string field) =>
        Errors.FirstOrDefault(e => string.Equals(e.Key, field, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

    private static string FirstMessage(string? title, IReadOnlyDictionary<string, string[]>? errors) =>
        errors?.Values.SelectMany(v => v).FirstOrDefault() ?? title ?? "Request failed";
}

/// <summary>The server couldn't be reached (no connection, timeout, or the proxy answering 502/503/504).</summary>
public class NetworkException(Exception? inner = null) : Exception("Can't reach the server. Check the connection and try again.", inner);

/// <summary>The session ended (refresh token expired or revoked): sign in again.</summary>
public class SessionExpiredException() : ApiException(401, "Your session has ended. Please sign in again.");

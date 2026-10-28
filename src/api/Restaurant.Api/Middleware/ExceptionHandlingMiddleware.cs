using System.Text.Json;
using Restaurant.Application.Common.Exceptions;

namespace Restaurant.Api.Middleware;

/// <summary>
/// Turns application exceptions into problem+json responses and makes sure nothing unexpected
/// (stack traces, internal messages) reaches the client.
/// </summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (status, title, errors) = exception switch
        {
            ValidationAppException validation => (StatusCodes.Status400BadRequest, "Please check the highlighted fields.", (object?)validation.Errors),
            NotFoundException => (StatusCodes.Status404NotFound, exception.Message, null),
            AuthenticationFailedException => (StatusCodes.Status401Unauthorized, exception.Message, null),
            ForbiddenAccessException => (StatusCodes.Status403Forbidden, exception.Message, null),
            TenantIsolationException => (StatusCodes.Status403Forbidden, "You don't have permission to do this.", null),
            ConflictException => (StatusCodes.Status409Conflict, exception.Message, null),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Request cancelled.", null),
            _ => (StatusCodes.Status500InternalServerError, "Something went wrong. Please try again.", null)
        };

        if (exception is TenantIsolationException)
        {
            logger.LogError(exception, "Tenant isolation violation on {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else
        {
            logger.LogInformation("{ExceptionType} on {Method} {Path}: {Message}", exception.GetType().Name, context.Request.Method, context.Request.Path, exception.Message);
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { title, status, errors, traceId = context.TraceIdentifier }, Json));
    }
}

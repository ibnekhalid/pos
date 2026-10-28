namespace Restaurant.Api.Middleware;

/// <summary>
/// Security headers on every API response. The API serves JSON and uploaded images only, never pages, so
/// the policy forbids scripts, frames and everything else outright. The web app's own headers are set by
/// its nginx (devops/nginx/web.conf).
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "cross-origin"; // logos are shown by the web app's origin
            headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            return Task.CompletedTask;
        });
        return next(context);
    }
}

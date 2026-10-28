using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Restaurant.Application.Common.Security;
using Restaurant.Contracts.Enums;

namespace Restaurant.Api.Auth;

/// <summary>
/// Turns the token into the request's TenantContext (PLAN.md section 4): loads the membership the
/// token names, rejects it if it's no longer active, then checks X-Branch-Id against the branches
/// the membership may use. Anonymous endpoints (sign-in, invites, PIN sign-in) skip all of this,
/// so a stale token sent along can't put them in the wrong tenant.
/// </summary>
public class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string BranchHeader = "X-Branch-Id";

    private static readonly string[] SelectionPaths = ["/api/auth/select-tenant", "/api/auth/logout", "/api/me"];

    public async Task InvokeAsync(HttpContext http, TenantContext context, MembershipAccessService access)
    {
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();
        context.SetCaller(null, remoteIp);

        var anonymous = http.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var user = http.User;
        if (anonymous || user.Identity?.IsAuthenticated != true || !Guid.TryParse(user.FindFirst(AppClaims.UserId)?.Value, out var userId))
        {
            await next(http);
            return;
        }

        context.SetCaller(userId, remoteIp);

        if (user.HasClaim(AppClaims.PendingSelection, "1")
            && !SelectionPaths.Any(p => http.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "Choose a restaurant first.");
            return;
        }

        if (Guid.TryParse(user.FindFirst(AppClaims.TenantId)?.Value, out var tenantId)
            && Guid.TryParse(user.FindFirst(AppClaims.MembershipId)?.Value, out var membershipId))
        {
            var membership = await access.GetAsync(tenantId, membershipId, http.RequestAborted);
            if (membership is null || membership.UserId != userId || membership.Status != MembershipStatus.Active)
            {
                await WriteProblemAsync(http, StatusCodes.Status401Unauthorized, "Your access to this restaurant has changed. Sign in again.");
                return;
            }
            if (membership.TenantStatus != TenantStatus.Active)
            {
                await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "This restaurant's account is suspended.");
                return;
            }
            context.SetMembership(membership);

            var branchHeader = http.Request.Headers[BranchHeader].ToString();
            if (!string.IsNullOrEmpty(branchHeader))
            {
                if (!Guid.TryParse(branchHeader, out var branchId) || !context.CanAccessBranch(branchId))
                {
                    await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "You don't have access to this branch.");
                    return;
                }
                context.SetBranch(branchId);
            }
        }

        await next(http);
    }

    private static Task WriteProblemAsync(HttpContext http, int status, string title)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(new { title, status, traceId = http.TraceIdentifier }));
    }
}

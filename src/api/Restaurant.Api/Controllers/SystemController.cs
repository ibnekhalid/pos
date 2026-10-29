using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Services;
using Restaurant.Contracts.System;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
public class SystemController(IHostEnvironment environment, AppReleases releases) : ControllerBase
{
    /// <summary>Lets the clients check they can reach the API (no sign-in needed).</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public ActionResult<ApiInfoDto> Info()
    {
        var version = typeof(SystemController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new ApiInfoDto("Restaurant POS API", version, environment.EnvironmentName);
    }

    /// <summary>The apps people can download and install (the "Get the apps" page, after sign-in).</summary>
    [HttpGet("app-releases")]
    [Authorize]
    public Task<IReadOnlyList<AppReleaseDto>> AppReleases(CancellationToken ct) => releases.ListAsync(ct);

    /// <summary>The newest app for one platform ("windows"), for its update notice (no sign-in needed).</summary>
    [HttpGet("app-releases/{platform}")]
    [AllowAnonymous]
    public async Task<ActionResult<AppReleaseDto>> AppRelease(string platform, CancellationToken ct) =>
        await releases.FindAsync(platform, ct) is { } release ? release : NoContent();

    /// <summary>The Windows app's release in its first shape (desktop apps up to 0.2 check this).</summary>
    [HttpGet("desktop-release")]
    [AllowAnonymous]
    public async Task<ActionResult<DesktopReleaseDto>> DesktopRelease(CancellationToken ct) =>
        await releases.FindAsync("windows", ct) is { } r ? new DesktopReleaseDto(r.Version, r.DownloadUrl, r.MinimumVersion) : new DesktopReleaseDto(null, null, null);

}

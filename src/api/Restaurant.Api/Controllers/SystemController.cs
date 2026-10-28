using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Contracts.System;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
public class SystemController(IHostEnvironment environment, IConfiguration configuration) : ControllerBase
{
    /// <summary>Lets the clients check they can reach the API (no sign-in needed).</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public ActionResult<ApiInfoDto> Info()
    {
        var version = typeof(SystemController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new ApiInfoDto("Restaurant POS API", version, environment.EnvironmentName);
    }

    /// <summary>
    /// The newest desktop app, for its update notice (no sign-in needed: it's checked on start-up).
    /// From configuration (DesktopRelease__LatestVersion etc. in production), read on each call.
    /// </summary>
    [HttpGet("desktop-release")]
    [AllowAnonymous]
    public ActionResult<DesktopReleaseDto> DesktopRelease()
    {
        var section = configuration.GetSection("DesktopRelease");
        return new DesktopReleaseDto(
            NullIfBlank(section["LatestVersion"]),
            NullIfBlank(section["DownloadUrl"]),
            NullIfBlank(section["MinimumVersion"]));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

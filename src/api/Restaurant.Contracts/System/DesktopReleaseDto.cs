namespace Restaurant.Contracts.System;

/// <summary>
/// Returned by GET /api/desktop-release — the newest Windows desktop app the counter PCs should run.
/// Set on the server under DesktopRelease (see docs/DEPLOYMENT.md, "Desktop app releases").
/// </summary>
/// <param name="LatestVersion">e.g. "0.3"; null when no release is published, so the app shows nothing.</param>
/// <param name="DownloadUrl">The installer (Restaurant-POS-Setup-x.y.exe).</param>
/// <param name="MinimumVersion">Older apps are told the update is required (still only a notice).</param>
public record DesktopReleaseDto(string? LatestVersion, string? DownloadUrl, string? MinimumVersion);

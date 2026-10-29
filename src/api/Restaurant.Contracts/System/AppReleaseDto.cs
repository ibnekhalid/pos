namespace Restaurant.Contracts.System;

/// <summary>One installable app (today only the Windows counter app; tablets and phones use the web app).</summary>
/// <param name="Platform">"windows".</param>
/// <param name="DownloadUrl">Absolute, on the site's /downloads/ (served by rp-web).</param>
/// <param name="MinimumVersion">Older installed apps are told the update is required (still only a notice).</param>
public record AppReleaseDto(string Platform, string Version, string DownloadUrl, string? MinimumVersion, long? SizeBytes, string? Sha256,
    DateTimeOffset? PublishedAt);

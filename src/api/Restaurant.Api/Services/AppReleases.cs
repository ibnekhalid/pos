using System.Text.Json;
using Restaurant.Contracts.System;

namespace Restaurant.Api.Services;

/// <summary>
/// The apps people can install, from releases.json in the downloads folder (written by the apps pipeline,
/// .github/workflows/apps.yml, next to the files it uploads):
/// <code>{ "windows": { "version": "0.3", "file": "Restaurant-POS-Setup-0.3.exe", "minimumVersion": null, "size": 1, "sha256": "...", "publishedAt": "..." } }</code>
/// Keyed by platform, so another installable app can be added later without an API change.
/// The DesktopRelease settings, when set, still override the Windows entry (the release steps before the pipeline).
/// The file is read on each call: publishing a release needs no API restart.
/// </summary>
public class AppReleases(IConfiguration configuration, IWebHostEnvironment environment, ILogger<AppReleases> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Entry(string? Version, string? File, string? MinimumVersion, long? Size, string? Sha256, DateTimeOffset? PublishedAt);

    public async Task<IReadOnlyList<AppReleaseDto>> ListAsync(CancellationToken ct)
    {
        var releases = new Dictionary<string, AppReleaseDto>();
        var path = configuration["Downloads:ManifestPath"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
            if (File.Exists(full))
            {
                try
                {
                    await using var stream = File.OpenRead(full);
                    var manifest = await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(stream, Json, ct) ?? [];
                    foreach (var (platform, entry) in manifest)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Version) || string.IsNullOrWhiteSpace(entry.File) || entry.File.Contains('/') || entry.File.Contains('\\')) continue;
                        var key = platform.Trim().ToLowerInvariant();
                        releases[key] = new AppReleaseDto(key, entry.Version.Trim(), Url(entry.File), Blank(entry.MinimumVersion), entry.Size, Blank(entry.Sha256), entry.PublishedAt);
                    }
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Could not read the app releases manifest {Path}", full);
                }
            }
        }

        var desktop = configuration.GetSection("DesktopRelease");
        if (Blank(desktop["LatestVersion"]) is { } version && Blank(desktop["DownloadUrl"]) is { } url)
        {
            releases["windows"] = new AppReleaseDto("windows", version, url, Blank(desktop["MinimumVersion"]), null, null, null);
        }
        return releases.Values.OrderBy(r => r.Platform == "windows" ? 0 : 1).ToList();
    }

    public async Task<AppReleaseDto?> FindAsync(string platform, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(r => r.Platform == platform.Trim().ToLowerInvariant());

    /// <summary>Under Downloads:PublicBaseUrl (e.g. https://restaurant.dsnoopdoc.com/downloads/).</summary>
    private string Url(string file)
    {
        var baseUrl = configuration["Downloads:PublicBaseUrl"] is { Length: > 0 } configured ? configured : "/downloads/";
        return baseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(file);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

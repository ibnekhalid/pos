using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Restaurant.Contracts.System;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>The "Get the apps" list and the apps' update check read releases.json from the downloads folder.</summary>
public class AppReleaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Signed_in_users_see_the_published_apps_and_the_apps_check_for_updates_anonymously()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "releases.json"), """
            {
              "windows": { "version": "0.4", "file": "Restaurant-POS-Setup-0.4.exe", "minimumVersion": "0.2", "size": 104857600, "sha256": "abc123", "publishedAt": "2026-10-28T10:00:00Z" },
              "kiosk": { "version": "1.0", "file": "../../etc/passwd" }
            }
            """);
        var api = factory.WithWebHostBuilder(b => b
            .UseSetting("Downloads:ManifestPath", Path.Combine(folder, "releases.json"))
            .UseSetting("Downloads:PublicBaseUrl", "https://restaurant.test/downloads/"));
        var auth = await TestApi.ReadAsync<Restaurant.Contracts.Auth.AuthResultDto>(api.CreateClient().PostAsJsonAsync("/api/auth/register-owner",
            new Restaurant.Contracts.Auth.RegisterOwnerRequest("Owner", TestApi.UniqueEmail(), null, TestApi.Password, "Apps test", null, "1 Mall Road", "Lahore"), TestApi.Json));
        var owner = new Caller(api.CreateClient(), auth);
        owner.Use(auth);

        var releases = await owner.GetAsync<List<AppReleaseDto>>("/api/app-releases");
        releases.Select(r => r.Platform).Should().Equal("windows"); // a file name with a path is ignored
        releases[0].Should().Match<AppReleaseDto>(r => r.DownloadUrl == "https://restaurant.test/downloads/Restaurant-POS-Setup-0.4.exe" && r.MinimumVersion == "0.2"
            && r.SizeBytes == 104857600);

        (await api.CreateClient().GetAsync("/api/app-releases")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var windows = await TestApi.ReadAsync<AppReleaseDto>(api.CreateClient().GetAsync("/api/app-releases/windows"));
        windows.Sha256.Should().Be("abc123");
        (await api.CreateClient().GetAsync("/api/app-releases/kiosk")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Desktop apps 0.2 still ask the old endpoint.
        var legacy = await TestApi.ReadAsync<DesktopReleaseDto>(api.CreateClient().GetAsync("/api/desktop-release"));
        legacy.LatestVersion.Should().Be("0.4");
    }
}

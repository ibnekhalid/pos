using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Restaurant.Contracts.System;

namespace Restaurant.Tests.Integration;

/// <summary>The API starts and its auth pipeline works. Needs no database (none of these endpoints touch it).</summary>
public class ApiSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.WithWebHostBuilder(b => b.UseSetting("environment", "Testing")).CreateClient();

    [Fact]
    public async Task Info_is_public()
    {
        var info = await _client.GetFromJsonAsync<ApiInfoDto>("/api/info");

        info.Should().NotBeNull();
        info!.Name.Should().Be("Restaurant POS API");
    }

    [Fact]
    public async Task Protected_endpoint_without_a_token_is_401()
    {
        var response = await _client.GetAsync("/api/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var response = await _client.GetAsync("/api/me");

        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Anonymous_callers_are_rate_limited_per_ip()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("environment", "Testing").UseSetting("RateLimiting:AnonymousPermitLimit", "3")).CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.GetAsync("/api/me")).StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/health")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }
}

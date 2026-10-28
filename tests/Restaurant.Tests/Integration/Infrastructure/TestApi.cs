using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Restaurant.Contracts.Auth;

namespace Restaurant.Tests.Integration.Infrastructure;

/// <summary>A signed-in API caller: one restaurant membership, optionally one selected branch.</summary>
public sealed class Caller(HttpClient http, AuthResultDto auth)
{
    public HttpClient Http { get; } = http;
    public AuthResultDto Auth { get; private set; } = auth;

    public Guid TenantId => Auth.TenantId!.Value;
    public Guid BranchId => Auth.DefaultBranchId!.Value;

    public void UseBranch(Guid? branchId)
    {
        Http.DefaultRequestHeaders.Remove("X-Branch-Id");
        if (branchId is not null)
        {
            Http.DefaultRequestHeaders.Add("X-Branch-Id", branchId.Value.ToString());
        }
    }

    public void Use(AuthResultDto auth)
    {
        Auth = auth;
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    }

    public Task<T> GetAsync<T>(string url) => TestApi.ReadAsync<T>(Http.GetAsync(url));
    public Task<T> PostAsync<T>(string url, object body) => TestApi.ReadAsync<T>(Http.PostAsJsonAsync(url, body, TestApi.Json));
    public Task<T> PutAsync<T>(string url, object body) => TestApi.ReadAsync<T>(Http.PutAsJsonAsync(url, body, TestApi.Json));

    public Task<HttpResponseMessage> RawGetAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> RawPostAsync(string url, object body) => Http.PostAsJsonAsync(url, body, TestApi.Json);
    public Task<HttpResponseMessage> RawPutAsync(string url, object body) => Http.PutAsJsonAsync(url, body, TestApi.Json);
    public Task<HttpResponseMessage> RawDeleteAsync(string url) => Http.DeleteAsync(url);
}

public static class TestApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public const string Password = "correct-horse-1";

    public static string UniqueEmail(string prefix = "owner") => $"{prefix}.{Guid.NewGuid():N}@test.local";

    /// <summary>A unique Pakistani mobile number in local format.</summary>
    public static string UniquePhone() => "03" + Random.Shared.NextInt64(100_000_000, 999_999_999);

    public static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public static Caller CallerFor(this ApiFactory factory, AuthResultDto auth)
    {
        var caller = new Caller(factory.CreateClient(), auth);
        caller.Use(auth);
        return caller;
    }

    public static async Task<Caller> RegisterOwnerAsync(this ApiFactory factory, string? restaurant = null, string? email = null, string? phone = null)
    {
        var anonymous = factory.CreateClient();
        var auth = await ReadAsync<AuthResultDto>(anonymous.PostAsJsonAsync("/api/auth/register-owner", new RegisterOwnerRequest(
            "Owner " + Guid.NewGuid().ToString("N")[..6], email ?? UniqueEmail(), phone, Password,
            restaurant ?? "Restaurant " + Guid.NewGuid().ToString("N")[..6], null, "1 Mall Road", "Lahore"), Json));
        return factory.CallerFor(auth);
    }

    public static async Task<AuthResultDto> LoginAsync(this ApiFactory factory, string login, string password = Password) =>
        await ReadAsync<AuthResultDto>(factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), Json));

    public static async Task<HttpResponseMessage> AnonymousPostAsync(this ApiFactory factory, string url, object body) =>
        await factory.CreateClient().PostAsJsonAsync(url, body, Json);

    public static void ShouldBe(this HttpResponseMessage response, HttpStatusCode status) =>
        response.StatusCode.Should().Be(status, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
}

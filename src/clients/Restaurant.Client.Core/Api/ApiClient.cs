using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Auth;

namespace Restaurant.Client.Core.Api;

/// <summary>
/// HTTP access to the API (ported from Tailor Book): attaches the access token and the selected
/// branch (X-Branch-Id), refreshes the token shortly before it expires (or once after a 401), and
/// turns failures into <see cref="ApiException"/> (the server said no) or
/// <see cref="NetworkException"/> (couldn't reach it).
/// </summary>
public class ApiClient(HttpClient http, SessionStore session)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Absolute URL for a path the API returned (logo images under /uploads).</summary>
    public string? ResolveUrl(string? path) =>
        string.IsNullOrEmpty(path) || Uri.IsWellFormedUriString(path, UriKind.Absolute) || http.BaseAddress is null
            ? path
            : new Uri(http.BaseAddress, path).ToString();

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, body, ct);
    public Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, path, body, ct);

    public async Task PostAsync(string path, object? body, CancellationToken ct = default) => await SendAsync<JsonElement?>(HttpMethod.Post, path, body, ct);
    public async Task PutAsync(string path, object? body, CancellationToken ct = default) => await SendAsync<JsonElement?>(HttpMethod.Put, path, body, ct);
    public async Task DeleteAsync(string path, CancellationToken ct = default) => await SendAsync<JsonElement?>(HttpMethod.Delete, path, null, ct);

    /// <summary>A signed-in GET with extra headers (the counter PC's device token).</summary>
    public async Task<T> GetAsync<T>(string path, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(() =>
        {
            var request = JsonRequest(HttpMethod.Get, path, null);
            foreach (var (name, value) in headers)
            {
                request.Headers.Add(name, value);
            }
            return request;
        }, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>Calls that need no session (sign-in, sign-up, invites, PIN sign-in).</summary>
    public async Task<T> SendAnonymousAsync<T>(HttpMethod method, string path, object? body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(() =>
        {
            var request = JsonRequest(method, path, body);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers.Add(name, value);
            }
            return request;
        }, token: null, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>Multipart upload (logo).</summary>
    public async Task<T> UploadAsync<T>(string path, string fieldName, byte[] content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(() =>
        {
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var form = new MultipartFormDataContent { { file, fieldName, fileName } };
            return new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        }, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(method, path, body), ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        var response = await SendRawAsync(createRequest, token, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null)
        {
            return response;
        }

        // Token refused (access changed, clock skew): refresh once and retry. If the refresh is also
        // refused the session is over.
        response.Dispose();
        token = await RefreshAsync(token, ct);
        return await SendRawAsync(createRequest, token, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> createRequest, string? token, CancellationToken ct)
    {
        using var request = createRequest();
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (session.Current?.BranchId is { } branchId)
            {
                request.Headers.Add("X-Branch-Id", branchId.ToString());
            }
        }

        try
        {
            var response = await http.SendAsync(request, ct);
            // The web app reaches the API through nginx, which answers 502/503/504 when the API is down:
            // the request never reached it, so treat that like no connection.
            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new NetworkException(new HttpRequestException($"Gateway returned {status}"));
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException(ex);
        }
    }

    /// <summary>For the live-updates connection: a current access token (refreshed if needed).</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            return await GetValidAccessTokenAsync(CancellationToken.None);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>Binary download (receipt PDF).</summary>
    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (!response.IsSuccessStatusCode)
        {
            await ReadAsync<JsonElement?>(response, ct);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>An access token good for at least 30 more seconds, refreshing first if needed.</summary>
    private async Task<string?> GetValidAccessTokenAsync(CancellationToken ct)
    {
        var token = session.Current?.AccessToken;
        if (token is null) return null;
        if (SecondsUntilExpiry(token) > 30) return token;
        return await RefreshAsync(token, ct);
    }

    private async Task<string> RefreshAsync(string staleToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited.
            var current = session.Current?.AccessToken;
            if (current is not null && current != staleToken && SecondsUntilExpiry(current) > 30)
            {
                return current;
            }

            var refreshToken = session.Current?.RefreshToken;
            if (refreshToken is null)
            {
                await session.SignOutAsync();
                throw new SessionExpiredException();
            }

            using var response = await SendRawAsync(
                () => JsonRequest(HttpMethod.Post, "api/auth/refresh", new RefreshTokenRequest(refreshToken)), token: null, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                await session.SignOutAsync();
                throw new SessionExpiredException();
            }

            var result = await ReadAsync<AuthResultDto>(response, ct);
            await session.ApplyRefreshAsync(result);
            return result.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json.Options), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            string? title = null;
            Dictionary<string, string[]>? errors = null;
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json.Options, ct);
                title = problem?.Title ?? problem?.Detail;
                errors = problem?.Errors;
            }
            catch
            {
                // Not JSON (a proxy error page).
            }
            throw new ApiException((int)response.StatusCode, title ?? $"Request failed ({(int)response.StatusCode})", errors);
        }

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default!;
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, ct))!;
    }

    private sealed record ProblemBody(string? Title, string? Detail, Dictionary<string, string[]>? Errors);

    /// <summary>Seconds until the JWT expires; 0 if it can't be read (treated as expired).</summary>
    public static double SecondsUntilExpiry(string token)
    {
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.GetProperty("exp").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }
}

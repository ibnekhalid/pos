using System.Text.Json;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Auth;

namespace Restaurant.Client.Core;

/// <summary>
/// The signed-in user in the current restaurant: permissions and usable branches (GET /api/me).
/// The screens use it to decide what to show; the API enforces the same rules regardless.
/// </summary>
public class UserContext
{
    private readonly RestaurantApi _api;
    private readonly SessionStore _session;
    private readonly IKeyValueStore _store;
    private readonly IPlatformInfo _platform;

    public UserContext(RestaurantApi api, SessionStore session, IKeyValueStore store, IPlatformInfo platform)
    {
        _api = api;
        _session = session;
        _store = store;
        _platform = platform;
        session.SessionEnded += () =>
        {
            Me = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        };
    }

    public MeDto? Me { get; private set; }
    public event Action? Changed;

    /// <summary>The API couldn't be reached: Me is the copy saved on this PC the last time it could (desktop only).</summary>
    public bool IsOffline { get; private set; }

    public bool IsOwner => Me?.IsOwner == true;
    public bool Can(string permission) => Me?.Permissions.Contains(permission) == true;
    public bool CanAny(params string[] permissions) => permissions.Any(Can);

    public IReadOnlyList<BranchSummaryDto> Branches => Me?.Branches ?? [];
    public BranchSummaryDto? CurrentBranch => Branches.FirstOrDefault(b => b.Id == _session.Current?.BranchId);

    public async Task LoadAsync()
    {
        try
        {
            Me = await _api.GetMeAsync();
            IsOffline = false;
            if (_platform.IsDesktop && _session.Current?.TenantId is { } tenantId)
            {
                await _store.SetAsync(CacheKey(tenantId), JsonSerializer.Serialize(Me, Json.Options));
            }
        }
        catch (NetworkException) when (_platform.IsDesktop)
        {
            // Offline: the screens use the permissions as they were; the API checks them again when orders sync.
            Me = await CachedAsync() ?? throw new NetworkException();
            IsOffline = true;
        }

        // Keep the selected branch only if it's still allowed; otherwise use the first one.
        if (CurrentBranch is null && Me.Branches.Count > 0)
        {
            await _session.SetBranchAsync(Me.Branches[0].Id);
        }
        Changed?.Invoke();
    }

    public async Task SwitchBranchAsync(Guid branchId)
    {
        if (Branches.Any(b => b.Id == branchId))
        {
            await _session.SetBranchAsync(branchId);
            Changed?.Invoke();
        }
    }

    private async Task<MeDto?> CachedAsync()
    {
        if (_session.Current?.TenantId is not { } tenantId) return null;
        try
        {
            var json = await _store.GetAsync(CacheKey(tenantId));
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<MeDto>(json, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CacheKey(Guid tenantId) => $"rp.me.{tenantId:N}";
}

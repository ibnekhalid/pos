using System.Text.Json;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Auth;

namespace Restaurant.Client.Core.Auth;

/// <summary>The signed-in session as kept on this device.</summary>
/// <param name="BranchId">Sent as X-Branch-Id on every call.</param>
/// <param name="IsPinSession">Started with a counter PIN: never written to storage.</param>
public record Session(
    string AccessToken,
    string? RefreshToken,
    bool RequiresTenantSelection,
    Guid? TenantId,
    Guid? BranchId,
    IReadOnlyList<TenantMembershipDto> Tenants,
    bool IsPinSession);

/// <summary>
/// Who is signed in, which branch is selected and whether the screen is locked. "Remember me"
/// sessions are stored on the device; other sessions (and every PIN session) live in memory only.
/// </summary>
public class SessionStore(ISecureStore secureStore)
{
    private const string SessionKey = "rp.session";

    public Session? Current { get; private set; }
    public bool RememberMe { get; private set; } = true;

    /// <summary>The counter screen is locked: the session is kept, but a PIN (or password) is needed to continue.</summary>
    public bool IsLocked { get; private set; }

    public bool IsSignedIn => Current is { RequiresTenantSelection: false, TenantId: not null };
    public string? TenantName => Current?.Tenants.FirstOrDefault(t => t.TenantId == Current.TenantId)?.TenantName;

    /// <summary>Sign-in, sign-out, restaurant or branch switch, lock/unlock.</summary>
    public event Action? Changed;

    /// <summary>Signed out or switched to another restaurant or user: per-user state must be dropped.</summary>
    public event Func<Task>? SessionEnded;

    public async Task InitializeAsync()
    {
        try
        {
            var json = await secureStore.GetAsync(SessionKey);
            Current = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Session>(json, Json.Options);
        }
        catch
        {
            Current = null;
        }
        RememberMe = true; // only remembered sessions are ever stored
        Changed?.Invoke();
    }

    /// <summary>A new sign-in (password, PIN, invite, restaurant selection).</summary>
    public async Task ApplySignInAsync(AuthResultDto result, bool rememberMe, bool pinSession = false)
    {
        var previous = Current;
        RememberMe = rememberMe;
        IsLocked = false;
        Current = new Session(
            result.AccessToken,
            result.RefreshToken,
            result.RequiresTenantSelection,
            result.TenantId,
            result.DefaultBranchId,
            result.Tenants,
            pinSession);

        if (previous is not null && (previous.TenantId != Current.TenantId || previous.AccessToken != Current.AccessToken))
        {
            await RaiseSessionEndedAsync();
        }
        await PersistAsync();
        Changed?.Invoke();
    }

    /// <summary>A token refresh: same user, restaurant and selected branch.</summary>
    public async Task ApplyRefreshAsync(AuthResultDto result)
    {
        if (Current is null) return;
        Current = Current with
        {
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken ?? Current.RefreshToken,
            Tenants = result.Tenants.Count > 0 ? result.Tenants : Current.Tenants
        };
        await PersistAsync();
    }

    public async Task SetBranchAsync(Guid branchId)
    {
        if (Current is null || Current.BranchId == branchId) return;
        Current = Current with { BranchId = branchId };
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        var hadSession = Current is not null;
        Current = null;
        IsLocked = false;
        try { await secureStore.RemoveAsync(SessionKey); } catch { /* nothing stored */ }
        if (hadSession)
        {
            await RaiseSessionEndedAsync();
        }
        Changed?.Invoke();
    }

    public void Lock()
    {
        if (Current is null || IsLocked) return;
        IsLocked = true;
        Changed?.Invoke();
    }

    private async Task PersistAsync()
    {
        try
        {
            if (RememberMe && Current is { IsPinSession: false })
            {
                await secureStore.SetAsync(SessionKey, JsonSerializer.Serialize(Current, Json.Options));
            }
            else
            {
                await secureStore.RemoveAsync(SessionKey);
            }
        }
        catch
        {
            // Storage unavailable (private browsing): the session just isn't remembered.
        }
    }

    private async Task RaiseSessionEndedAsync()
    {
        if (SessionEnded is null) return;
        foreach (var handler in SessionEnded.GetInvocationList().Cast<Func<Task>>())
        {
            await handler();
        }
    }
}

using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
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

    public UserContext(RestaurantApi api, SessionStore session)
    {
        _api = api;
        _session = session;
        session.SessionEnded += () =>
        {
            Me = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        };
    }

    public MeDto? Me { get; private set; }
    public event Action? Changed;

    public bool IsOwner => Me?.IsOwner == true;
    public bool Can(string permission) => Me?.Permissions.Contains(permission) == true;
    public bool CanAny(params string[] permissions) => permissions.Any(Can);

    public IReadOnlyList<BranchSummaryDto> Branches => Me?.Branches ?? [];
    public BranchSummaryDto? CurrentBranch => Branches.FirstOrDefault(b => b.Id == _session.Current?.BranchId);

    public async Task LoadAsync()
    {
        Me = await _api.GetMeAsync();

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
}

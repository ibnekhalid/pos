using Microsoft.AspNetCore.Components;
using Restaurant.Client.Core.Api;

namespace Restaurant.Client.UI.Components;

/// <summary>
/// Base for screens that load data and run actions: <see cref="LoadAsync"/> on open, and
/// <see cref="RunAsync"/> for button actions (busy flag against double clicks, error and success
/// messages, field errors from the API's validation response).
/// </summary>
public abstract class PageBase : RpComponent
{
    [Inject] protected RestaurantApi Api { get; set; } = default!;

    protected bool Loading { get; private set; } = true;
    protected bool Busy { get; private set; }
    protected string? Error { get; set; }
    protected string? Success { get; set; }
    private ApiException? _lastApiError;

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task ReloadAsync()
    {
        try
        {
            Error = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Runs a user action once at a time; returns false (with Error set) if it failed.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        if (Busy) return false;
        Busy = true;
        Error = null;
        Success = null;
        _lastApiError = null;
        StateHasChanged();
        try
        {
            await action();
            Success = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            _lastApiError = ex as ApiException;
            Error = Describe(ex);
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    /// <summary>The API's validation message for a field from the last failed action.</summary>
    protected string? FieldError(string field) => _lastApiError?.FieldError(field);

    protected string Describe(Exception ex) => ex switch
    {
        NetworkException => T("common.networkError"),
        ApiException api => api.Message,
        _ => T("common.error")
    };
}

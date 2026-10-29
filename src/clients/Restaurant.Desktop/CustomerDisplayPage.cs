using Microsoft.AspNetCore.Components.WebView.Maui;
using Restaurant.Client.UI;

namespace Restaurant.Desktop;

/// <summary>The customer-facing screen: the same shared app, opened straight at /app/display in a window of its own.</summary>
public class CustomerDisplayPage : ContentPage
{
    public CustomerDisplayPage()
    {
        var view = new BlazorWebView { HostPage = "wwwroot/index.html", StartPath = "/app/display" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(AppRoot) });
        Content = view;
    }
}

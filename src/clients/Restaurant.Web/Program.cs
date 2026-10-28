using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Restaurant.Client.Core.Platform;
using Restaurant.Client.UI;
using Restaurant.Web;
using Restaurant.Web.Platform;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Production: the API is on the same origin (nginx forwards /api). wwwroot/appsettings.Development.json
// points local `dotnet run` at the dev API on :5080 instead.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
var apiBase = string.IsNullOrWhiteSpace(apiBaseUrl) ? builder.HostEnvironment.BaseAddress : apiBaseUrl.TrimEnd('/') + "/";
builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(60) });

builder.Services.AddSingleton<BrowserInterop>();
builder.Services.AddSingleton<IKeyValueStore, LocalStorageStore>();
builder.Services.AddSingleton<ISecureStore, LocalStorageSecureStore>();
builder.Services.AddSingleton<IConnectivity, BrowserConnectivity>();
builder.Services.AddSingleton<IWindowService, BrowserFullScreen>();
builder.Services.AddSingleton<IPlatformInfo, WebPlatformInfo>();
builder.Services.AddRestaurantUi();

await builder.Build().RunAsync();

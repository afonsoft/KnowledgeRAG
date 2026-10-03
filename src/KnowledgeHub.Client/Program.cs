using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using KnowledgeHub.Client;
using KnowledgeHub.Client.Services;

using System.Globalization;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBootstrapBlazor();
builder.Services.AddAuthorizationCore();

// SPEC-20260914-auth-login: the shared HttpClient flows the session cookie
// (same-origin) and routes 401/403-gate responses to the auth screens.
builder.Services.AddTransient<AuthRedirectHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthRedirectHandler>();
    handler.InnerHandler = new HttpClientHandler();
    // SPEC-20260916-firecrawl-mcp-proxy: upstream tools like firecrawl_crawl
    // poll to a terminal state and can exceed the 100s default timeout.
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
        Timeout = TimeSpan.FromMinutes(6)
    };
});
builder.Services.AddScoped<AuthApiClient>();
builder.Services.AddScoped<KhAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<KhAuthenticationStateProvider>());

builder.Services.AddScoped<SourceApiClient>();
builder.Services.AddScoped<SearchApiClient>();
builder.Services.AddScoped<ToolsApiClient>();
builder.Services.AddScoped<ApprovalsApiClient>();
builder.Services.AddScoped<ThreadsApiClient>();
builder.Services.AddScoped<StreamingApiClient>();
builder.Services.AddScoped<A2aApiClient>();
builder.Services.AddScoped<SettingsApiClient>();
builder.Services.AddScoped<EvalApiClient>();
builder.Services.AddScoped<GraphApiClient>();
builder.Services.AddScoped<FlowsApiClient>();
builder.Services.AddTransient<McpMonitorClient>();
builder.Services.AddLocalization();

var host = builder.Build();

// Restore the persisted culture BEFORE the first render so every
// IStringLocalizer lookup resolves against the right satellite.
var js = host.Services.GetRequiredService<IJSRuntime>();
var culture = await js.InvokeAsync<string?>("localStorage.getItem", "kh:culture");
if (!string.IsNullOrWhiteSpace(culture))
{
    var ci = new CultureInfo(culture);
    CultureInfo.DefaultThreadCurrentCulture = ci;
    CultureInfo.DefaultThreadCurrentUICulture = ci;
}

await host.RunAsync();

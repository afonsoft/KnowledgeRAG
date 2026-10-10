using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace KnowledgeHub.Tests.Ui;

/// <summary>
/// Boots the real KnowledgeHub server (dotnet run, SQLite temp DB, offline
/// connectors) and a headless Chromium for Playwright UI tests. The seeded
/// admin password is rotated once through the API so UI logins use a known
/// credential instead of hitting the forced-change gate.
/// </summary>
public sealed class UiFixture : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "UiTest-12345";
    private const string SeededPassword = "123qwe";

    private readonly StringBuilder _serverLog = new();
    private Process? _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _dbPath = "";

    public string BaseUrl { get; private set; } = "";
    public string ServerLog => _serverLog.ToString();
    public string ScreenshotDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "ui-screenshots");

    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("fixture not initialized");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ScreenshotDir);
        var repoRoot = FindRepoRoot();
        _dbPath = Path.Combine(Path.GetTempPath(), $"kh-ui-{Guid.NewGuid():N}.db");

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo("dotnet",
            $"run --project \"{Path.Combine(repoRoot, "src", "KnowledgeHub.Server")}\" --no-launch-profile")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // The VM exports provider overrides that break ConfigurationValidator.
        psi.Environment.Remove("Database__Provider");
        psi.Environment.Remove("GameHub_Database__Provider");
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // The hosted WASM client resolves through the dev-time static web
        // assets manifest (*.staticwebassets.runtime.json) — Development is
        // required for MapStaticAssets/UseStaticWebAssets to serve it.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["Database__Path"] = _dbPath;
        psi.Environment["DeepWiki__Enabled"] = "false";
        psi.Environment["Embeddings__Provider"] = "deterministic";

        _server = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start server");
        _server.OutputDataReceived += (_, e) => { if (e.Data is not null) _serverLog.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) _serverLog.AppendLine(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        await WaitForServerAsync();
        await RotateAdminPasswordAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        try
        {
            _server?.Kill(entireProcessTree: true);
            _server?.WaitForExit(10_000);
        }
        catch { /* best effort */ }

        _server?.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }

    private async Task WaitForServerAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_server!.HasExited)
                throw new InvalidOperationException(
                    $"server exited early ({_server.ExitCode}):\n{_serverLog}");
            try
            {
                var resp = await http.GetAsync($"{BaseUrl}/health/live");
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) { /* not up yet */ }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"server did not become healthy:\n{_serverLog}");
    }

    private async Task RotateAdminPasswordAsync()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        var login = await http.PostAsync("/api/auth/login", Json(new
        {
            username = AdminUsername,
            password = SeededPassword
        }));
        login.EnsureSuccessStatusCode();

        var change = await http.PostAsync("/api/auth/change-password", Json(new
        {
            currentPassword = SeededPassword,
            newPassword = AdminPassword
        }));
        change.EnsureSuccessStatusCode();
    }

    private static StringContent Json(object body) => new(
        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnowledgeHub.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("KnowledgeHub.slnx not found above test output");
    }
}

using Microsoft.Playwright;

namespace KnowledgeHub.Tests.Ui;

/// <summary>
/// Playwright UI tests against the real server (see <see cref="UiFixture"/>):
/// visual screenshots of every page, a no-JS-errors sweep, the mobile nav
/// drawer + horizontal-overflow check, the i18n switcher, auth redirect,
/// sidebar click-through and basic a11y attributes.
/// </summary>
public class UiTests : IClassFixture<UiFixture>
{
    private static readonly string[] AppRoutes =
    [
        "/", "/sources", "/mcp-monitor", "/approvals", "/chat", "/playground",
        "/flows", "/graph", "/eval", "/rag-quality", "/settings", "/api-keys"
    ];

    private readonly UiFixture _fx;

    public UiTests(UiFixture fx) => _fx = fx;

    private async Task<(IBrowserContext Ctx, IPage Page, List<string> Errors)> NewPageAsync(
        int? width = null, int? height = null)
    {
        var ctx = await _fx.Browser.NewContextAsync(width is null
            ? null
            : new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width.Value, Height = height!.Value }
            });
        var errors = new List<string>();
        var page = await ctx.NewPageAsync();
        page.Console += (_, m) =>
        {
            if (m.Type == "error")
                lock (errors) errors.Add($"{m.Text} [{m.Location}]");
        };
        page.PageError += (_, e) =>
        {
            lock (errors) errors.Add(e.ToString());
        };
        return (ctx, page, errors);
    }

    private string Shot(string name) =>
        Path.Combine(_fx.ScreenshotDir, name + ".png");

    private async Task LoginViaFormAsync(IPage page)
    {
        await page.GotoAsync($"{_fx.BaseUrl}/login");
        await page.WaitForSelectorAsync("#login-username",
            new() { Timeout = 120_000 }); // first WASM boot downloads the app
        await page.FillAsync("#login-username", UiFixture.AdminUsername);
        await page.FillAsync("#login-password", UiFixture.AdminPassword);
        await page.ClickAsync("button.btn-primary");
        await page.WaitForSelectorAsync(".sidebar", new() { Timeout = 60_000 });
    }

    private static IEnumerable<string> RealErrors(IEnumerable<string> errors) =>
        errors.Where(e =>
            !e.Contains("favicon", StringComparison.OrdinalIgnoreCase) &&
            // The WASM client polls auth state on boot — the anonymous 401 is expected.
            !e.Contains("/api/auth/me", StringComparison.OrdinalIgnoreCase));

    private async Task GotoAndSettleAsync(IPage page, string route)
    {
        await page.GotoAsync(_fx.BaseUrl + route);
        await page.WaitForSelectorAsync(".top-row-title", new() { Timeout = 120_000 });
        await page.WaitForTimeoutAsync(1500); // let deferred renders/queries land
    }

    [Fact]
    public async Task Login_ViaForm_ShowsDashboard()
    {
        var (ctx, page, errors) = await NewPageAsync();
        await LoginViaFormAsync(page);

        var title = await page.TextContentAsync(".top-row-title");
        Assert.False(string.IsNullOrWhiteSpace(title));
        await page.ScreenshotAsync(new() { Path = Shot("01-login-home"), FullPage = true });
        Assert.True(!RealErrors(errors).Any(), string.Join("\n", RealErrors(errors)));
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Login_WrongPassword_ShowsError()
    {
        var (ctx, page, _) = await NewPageAsync();
        await page.GotoAsync($"{_fx.BaseUrl}/login");
        await page.WaitForSelectorAsync("#login-username", new() { Timeout = 120_000 });
        await page.FillAsync("#login-username", UiFixture.AdminUsername);
        await page.FillAsync("#login-password", "wrong-pass-1");
        await page.ClickAsync("button.btn-primary");

        var alert = await page.WaitForSelectorAsync(".alert-danger", new() { Timeout = 30_000 });
        Assert.NotNull(alert);
        Assert.False(string.IsNullOrWhiteSpace(await alert.TextContentAsync()));
        await page.ScreenshotAsync(new() { Path = Shot("02-login-error"), FullPage = true });
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AllPages_Render_WithoutJsErrors()
    {
        var (ctx, page, errors) = await NewPageAsync();
        await LoginViaFormAsync(page);

        var failures = new List<string>();
        foreach (var route in AppRoutes)
        {
            var mark = errors.Count;
            await GotoAndSettleAsync(page, route);
            var title = await page.TextContentAsync(".top-row-title");
            if (string.IsNullOrWhiteSpace(title))
                failures.Add($"{route}: empty page title");
            if (RealErrors(errors.Skip(mark)).Any())
                failures.Add($"{route}: {string.Join(" | ", errors.Skip(mark))}");

            var slug = route == "/" ? "home" : route.Trim('/').Replace('/', '-');
            await page.ScreenshotAsync(new() { Path = Shot($"page-{slug}"), FullPage = true });
        }

        Assert.True(failures.Count == 0,
            string.Join('\n', failures) + "\n\nserver log tail:\n" +
            string.Join('\n', _fx.ServerLog.Split('\n').TakeLast(30)));
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task MobileViewport_Drawer_NoHorizontalOverflow()
    {
        var (ctx, page, _) = await NewPageAsync(375, 812);
        await LoginViaFormAsync(page);

        await page.ClickAsync(".kh-nav-toggler");
        await page.WaitForSelectorAsync(".sidebar.show", new() { Timeout = 10_000 });
        await page.ClickAsync(".sidebar-close");
        await page.WaitForSelectorAsync(".sidebar.show",
            new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        await page.ScreenshotAsync(new() { Path = Shot("mobile-home"), FullPage = true });

        foreach (var route in new[] { "/", "/sources", "/api-keys" })
        {
            await GotoAndSettleAsync(page, route);
            var scrollW = await page.EvaluateAsync<int>(
                "() => document.scrollingElement.scrollWidth");
            Assert.True(scrollW <= 376, $"{route}: horizontal overflow {scrollW}px > 375px");
        }

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task I18n_Switcher_RendersPtBrAndEs()
    {
        var (ctx, page, _) = await NewPageAsync();
        await LoginViaFormAsync(page);

        await page.SelectOptionAsync(".culture-selector select", "pt-BR");
        await page.WaitForSelectorAsync(".sidebar", new() { Timeout = 60_000 });
        Assert.Contains("Fontes", await page.InnerTextAsync(".sidebar"));

        await page.SelectOptionAsync(".culture-selector select", "es");
        await page.WaitForSelectorAsync(".sidebar", new() { Timeout = 60_000 });
        Assert.Contains("Fuentes", await page.InnerTextAsync(".sidebar"));

        await page.SelectOptionAsync(".culture-selector select", "en");
        await page.WaitForSelectorAsync(".sidebar", new() { Timeout = 60_000 });
        Assert.Contains("Sources", await page.InnerTextAsync(".sidebar"));

        await page.ScreenshotAsync(new() { Path = Shot("i18n-en"), FullPage = true });
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_RedirectsToLogin()
    {
        var (ctx, page, _) = await NewPageAsync();
        await page.GotoAsync($"{_fx.BaseUrl}/sources");
        await page.WaitForSelectorAsync("#login-username", new() { Timeout = 120_000 });
        Assert.Contains("/login", page.Url);
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task SidebarNav_ClickThrough_ToSources()
    {
        var (ctx, page, _) = await NewPageAsync();
        await LoginViaFormAsync(page);

        await page.ClickAsync(".sidebar a.nav-link[href='sources']");
        await page.WaitForSelectorAsync(".top-row-title", new() { Timeout = 30_000 });
        Assert.EndsWith("/sources", page.Url);
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task A11y_BasicAttributes()
    {
        var (ctx, page, _) = await NewPageAsync();
        await LoginViaFormAsync(page);

        var lang = await page.GetAttributeAsync("html", "lang");
        Assert.False(string.IsNullOrWhiteSpace(lang));

        var toggler = await page.GetAttributeAsync(".kh-nav-toggler", "aria-label");
        Assert.False(string.IsNullOrWhiteSpace(toggler));

        var culture = await page.GetAttributeAsync(".culture-selector select", "aria-label");
        Assert.False(string.IsNullOrWhiteSpace(culture));

        var imgsMissingAlt = await page.EvaluateAsync<int>(
            "() => [...document.images].filter(i => !i.hasAttribute('alt')).length");
        Assert.Equal(0, imgsMissingAlt);
        await ctx.DisposeAsync();
    }
}

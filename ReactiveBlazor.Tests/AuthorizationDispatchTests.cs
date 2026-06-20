using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ReactiveBlazor.Tests;

/// <summary>
/// Integration tests for declarative authorization. Authentication is simulated with a middleware
/// that builds <see cref="HttpContext.User"/> from per-request headers:
/// <c>X-Test-User</c> (name, presence ⇒ authenticated) and <c>X-Test-Roles</c> (comma-separated).
/// This exercises the same <see cref="IAuthorizationService"/> path the framework uses in production.
/// </summary>
public class AuthorizationDispatchTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly IReactiveStateCodec _codec;

    public AuthorizationDispatchTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddRazorComponents();
                    services.AddDataProtection().SetApplicationName("ReactiveBlazor.AuthTests");
                    services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
                    services.AddHttpContextAccessor();

                    // Register an authentication scheme so the endpoint can distinguish an
                    // auth-enabled app (antiforgery failure while unauthenticated ⇒ 401) from one
                    // with no auth at all (⇒ 400). The scheme handler is never actually invoked —
                    // the test middleware sets HttpContext.User directly.
                    services.AddAuthentication("TestAuth")
                        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NoopAuthHandler>("TestAuth", _ => { });

                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy("AdminOnly", p => p.RequireRole("admin"));
                        options.AddPolicy("Adult", p => p.RequireClaim("age", "adult"));
                    });

                    services.Configure<ReactiveOptions>(_ => { });

                    var registry = new ReactiveComponentRegistry();
                    registry.Register(typeof(AuthorizedAction));
                    registry.Register(typeof(AdminComponent));
                    registry.Register(typeof(AdultPolicyComponent));
                    registry.Register(typeof(MissingPolicyComponent));
                    registry.Register(typeof(AnonymousAllowedComponent));
                    registry.Register(typeof(AuthViewComponent));
                    registry.Register(typeof(AuthSignalPublisher));
                    registry.Register(typeof(AdminSignalSubscriber));
                    registry.Freeze();
                    services.AddSingleton(registry);
                    services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
                    services.AddScoped<IReactiveSignals, ReactiveSignals>();
                    services.AddSingleton<IReactiveNonceStore, InMemoryReactiveNonceStore>();
                });
                web.Configure(app =>
                {
                    // Simulate authentication from test headers, before routing.
                    app.Use(async (ctx, next) =>
                    {
                        var name = ctx.Request.Headers["X-Test-User"].ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            var claims = new List<Claim> { new(ClaimTypes.Name, name) };
                            var roles = ctx.Request.Headers["X-Test-Roles"].ToString();
                            if (!string.IsNullOrEmpty(roles))
                                claims.AddRange(roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Select(r => new Claim(ClaimTypes.Role, r)));
                            var extra = ctx.Request.Headers["X-Test-Claims"].ToString();
                            if (!string.IsNullOrEmpty(extra))
                                foreach (var pair in extra.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var kv = pair.Split('=');
                                    if (kv.Length == 2) claims.Add(new Claim(kv[0].Trim(), kv[1].Trim()));
                                }
                            // Authentication type non-empty ⇒ IsAuthenticated == true.
                            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
                        }
                        await next();
                    });

                    app.UseRouting();
                    app.UseAntiforgery();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapReactiveComponents();
                        endpoints.MapGet("/_test/antiforgery", (
                            Microsoft.AspNetCore.Antiforgery.IAntiforgery af, HttpContext ctx) =>
                        {
                            var tokens = af.GetAndStoreTokens(ctx);
                            return Results.Ok(new { token = tokens.RequestToken });
                        });
                    });
                });
            })
            .Build();

        _host.Start();
        _client = _host.GetTestClient();
        _codec = _host.Services.GetRequiredService<IReactiveStateCodec>();
    }

    // Antiforgery tokens bind to the authenticated user, so each identity needs its own
    // token+cookie fetched under that same identity (matching the headers used for the dispatch).
    private (string token, string cookie) GetAntiforgery(string? user, string? roles, string? claims)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/_test/antiforgery");
        if (user is not null) req.Headers.Add("X-Test-User", user);
        if (roles is not null) req.Headers.Add("X-Test-Roles", roles);
        if (claims is not null) req.Headers.Add("X-Test-Claims", claims);
        var resp = _client.SendAsync(req).GetAwaiter().GetResult();
        var cookie = string.Join("; ", resp.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var body = resp.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        return (body.GetProperty("token").GetString()!, cookie);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // Builds a single-component dispatch with optional identity headers.
    private HttpRequestMessage Dispatch(
        Type type, string componentId, string? action,
        string? user = null, string? roles = null, string? claims = null)
    {
        var state = _codec.Protect(type, $$"""{"ComponentId":"{{componentId}}"}""");
        var body = new
        {
            targetId = componentId,
            action,
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[] { new { id = componentId, state } }
        };
        var (token, cookie) = GetAntiforgery(user, roles, claims);
        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("Cookie", cookie);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (roles is not null) request.Headers.Add("X-Test-Roles", roles);
        if (claims is not null) request.Headers.Add("X-Test-Claims", claims);
        return request;
    }

    // ── Action-level authorization ─────────────────────────────────────────

    [Fact]
    public async Task AuthorizedAction_AuthenticatedUser_Returns200()
    {
        var response = await _client.SendAsync(Dispatch(typeof(AuthorizedAction), "a1", "DoIt", user: "alice"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedAction_Unauthenticated_Returns401()
    {
        var response = await _client.SendAsync(Dispatch(typeof(AuthorizedAction), "a2", "DoIt"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_AntiforgeryFails_Unauthenticated_Returns401_NotBadRequest()
    {
        // Simulates an idle session expiring: the antiforgery token (bound to the prior user) is now
        // invalid AND the request is unauthenticated. In an auth-enabled app this must surface as 401
        // (client reloads to login), not the generic 400 — otherwise the user is stuck on an error toast.
        var state = _codec.Protect(typeof(AuthorizedAction), """{"ComponentId":"exp1"}""");
        var body = new
        {
            targetId = "exp1",
            action = "DoIt",
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[] { new { id = "exp1", state } }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        // No antiforgery header/cookie and no identity ⇒ antiforgery validation fails while unauthenticated.
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RoleAction_WrongRole_Returns403()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AdminComponent), "a3", "AdminAction", user: "bob", roles: "user"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleAction_CorrectRole_Returns200()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AdminComponent), "a4", "AdminAction", user: "carol", roles: "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RoleAction_Unauthenticated_Returns401()
    {
        var response = await _client.SendAsync(Dispatch(typeof(AdminComponent), "a5", "AdminAction"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymousAction_OnAuthorizedComponent_BypassesAuth()
    {
        // Component requires admin, but the action is [AllowAnonymous] ⇒ runs even unauthenticated.
        var response = await _client.SendAsync(Dispatch(typeof(AdminComponent), "a6", "PublicAction"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PolicyAction_SatisfiesPolicy_Returns200()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AdultPolicyComponent), "a7", "DoIt", user: "dora", claims: "age=adult"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PolicyAction_FailsPolicy_Returns403()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AdultPolicyComponent), "a8", "DoIt", user: "eve"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingPolicy_FailsClosed_Returns403_NotServerError()
    {
        // References a policy name that was never registered ⇒ resolution throws ⇒ fail closed (403).
        var response = await _client.SendAsync(
            Dispatch(typeof(MissingPolicyComponent), "a9", "DoIt", user: "frank", roles: "admin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── Component-level authorization ──────────────────────────────────────

    [Fact]
    public async Task TargetedComponentDenied_EmptyBoundary_NoStateToken_ActionDidNotRun()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AdminComponent), "c1", "RecordSideEffect", user: "bob", roles: "user"));

        // Component-level [Authorize] denies BEFORE the action: forbidden, and the side effect never ran.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(AdminComponent.SideEffectRan);
    }

    [Fact]
    public async Task DeniedComponentRender_SuppressesContentAndState()
    {
        // Render the component directly via HtmlRenderer with a denied (unauthenticated) context.
        var html = await RenderComponentAsync(typeof(AdminComponent), user: null);
        Assert.Contains("data-reactive-denied", html);
        Assert.DoesNotContain("data-state", html);
        Assert.DoesNotContain("admin-secret", html);
    }

    [Fact]
    public async Task AuthorizedComponentRender_EmitsStateAndContent()
    {
        var html = await RenderComponentAsync(typeof(AdminComponent), user: "carol", roles: "admin");
        Assert.Contains("data-state", html);
        Assert.Contains("admin-secret", html);
        Assert.DoesNotContain("data-reactive-denied", html);
    }

    // ── Signal/OOB siblings ────────────────────────────────────────────────

    private async Task<Dictionary<string, string>?> PublishWithAdminSubscriber(string user, string roles)
    {
        var pubState = _codec.Protect(typeof(AuthSignalPublisher), """{"ComponentId":"pub"}""");
        var subState = _codec.Protect(typeof(AdminSignalSubscriber), """{"ComponentId":"sub"}""");

        var body = new
        {
            targetId = "pub",
            action = "Publish",
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[]
            {
                new { id = "pub", state = pubState },
                new { id = "sub", state = subState }
            }
        };
        var (token, cookie) = GetAntiforgery(user, roles, null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Test-User", user);
        request.Headers.Add("X-Test-Roles", roles);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
    }

    [Fact]
    public async Task UnauthorizedSibling_OmittedFromSignalRefresh()
    {
        // Authenticated but NOT admin: the admin-only subscriber must be omitted.
        var updates = await PublishWithAdminSubscriber("george", "user");
        Assert.NotNull(updates);
        Assert.True(updates!.ContainsKey("pub"));
        Assert.False(updates.ContainsKey("sub"));
    }

    [Fact]
    public async Task AuthorizedSibling_IncludedInSignalRefresh()
    {
        var updates = await PublishWithAdminSubscriber("hannah", "admin");
        Assert.NotNull(updates);
        Assert.True(updates!.ContainsKey("pub"));
        Assert.True(updates.ContainsKey("sub"));
    }

    // ── <AuthorizeView> works during dispatch (seeded auth state) ──────────

    [Fact]
    public async Task AuthorizeView_RendersAuthorizedBranch_ForAuthenticatedUser()
    {
        var response = await _client.SendAsync(
            Dispatch(typeof(AuthViewComponent), "v1", "Noop", user: "ian"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updates = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Contains("signed-in", updates!["v1"]);
        Assert.DoesNotContain("signed-out", updates["v1"]);
    }

    [Fact]
    public async Task AuthorizeView_RendersNotAuthorizedBranch_ForAnonymousUser()
    {
        var response = await _client.SendAsync(Dispatch(typeof(AuthViewComponent), "v2", "Noop"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updates = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Contains("signed-out", updates!["v2"]);
        Assert.DoesNotContain("signed-in", updates["v2"]);
    }

    // ── Anonymous-allowed component keeps working without auth ─────────────

    [Fact]
    public async Task AnonymousComponent_NoAuth_Returns200()
    {
        var response = await _client.SendAsync(Dispatch(typeof(AnonymousAllowedComponent), "n1", "Ping"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helper: render a component through the same auth-seeded path ────────

    private async Task<string> RenderComponentAsync(Type type, string? user = null, string? roles = null)
    {
        var claims = new List<Claim>();
        ClaimsPrincipal principal;
        if (user is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, user));
            if (roles is not null)
                claims.AddRange(roles.Split(',').Select(r => new Claim(ClaimTypes.Role, r.Trim())));
            principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }
        else
        {
            principal = new ClaimsPrincipal(new ClaimsIdentity());
        }

        using var scope = _host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var authService = sp.GetRequiredService<IAuthorizationService>();
        var policyProvider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        var authContext = new ReactiveAuthorizationContext(
            principal, authService, policyProvider, loggerFactory.CreateLogger("test"));
        await authContext.PrecomputeAsync(new[] { type });

        var renderServices = new RenderTestServiceProvider(sp, new SeededAuthenticationStateProvider(principal), authContext);
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(renderServices, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var p = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ReactiveState"] = $$"""{"ComponentId":"render1"}"""
            });
            var output = await renderer.RenderComponentAsync(type, p);
            return output.ToHtmlString();
        });
    }

    private sealed class RenderTestServiceProvider(
        IServiceProvider inner,
        AuthenticationStateProvider auth,
        IReactiveAuthorizationContext ctx) : IServiceProvider
    {
        public object? GetService(Type t) =>
            t == typeof(IServiceProvider) ? this
            : t == typeof(AuthenticationStateProvider) ? auth
            : t == typeof(IReactiveAuthorizationContext) ? ctx
            : inner.GetService(t);
    }
}

// ── Test infrastructure ───────────────────────────────────────────────────────

/// <summary>
/// A no-op authentication handler. It is never invoked (the test middleware sets
/// <c>HttpContext.User</c> directly); registering it simply makes
/// <c>IAuthenticationSchemeProvider</c> report a scheme, so the endpoint treats the host as an
/// auth-enabled app.
/// </summary>
internal sealed class NoopAuthHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public NoopAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
}

// ── Test components ──────────────────────────────────────────────────────────

[Authorize]
public class AuthorizedAction : ReactiveComponent
{
    [ReactiveAction] public void DoIt() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "ok")));
        builder.CloseComponent();
    }
}

[Authorize(Roles = "admin")]
public class AdminComponent : ReactiveComponent
{
    public static bool SideEffectRan;

    [ReactiveAction] public void AdminAction() { }

    [ReactiveAction] public void RecordSideEffect() => SideEffectRan = true;

    [AllowAnonymous]
    [ReactiveAction] public void PublicAction() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "admin-secret")));
        builder.CloseComponent();
    }
}

[Authorize(Policy = "Adult")]
public class AdultPolicyComponent : ReactiveComponent
{
    [ReactiveAction] public void DoIt() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "adult")));
        builder.CloseComponent();
    }
}

[Authorize(Policy = "ThisPolicyDoesNotExist")]
public class MissingPolicyComponent : ReactiveComponent
{
    [ReactiveAction] public void DoIt() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "x")));
        builder.CloseComponent();
    }
}

public class AnonymousAllowedComponent : ReactiveComponent
{
    [ReactiveAction] public void Ping() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "pong")));
        builder.CloseComponent();
    }
}

public class AuthViewComponent : ReactiveComponent
{
    [ReactiveAction] public void Noop() { }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb =>
        {
            cb.OpenComponent<AuthorizeView>(0);
            cb.AddAttribute(1, "Authorized", (RenderFragment<AuthenticationState>)(_ => b => b.AddContent(0, "signed-in")));
            cb.AddAttribute(2, "NotAuthorized", (RenderFragment<AuthenticationState>)(_ => b => b.AddContent(0, "signed-out")));
            cb.CloseComponent();
        }));
        builder.CloseComponent();
    }
}

public sealed record AuthTestSignal : IReactiveSignal;

public class AuthSignalPublisher : ReactiveComponent
{
    [ReactiveAction] public void Publish() => ReactiveSignals.Publish<AuthTestSignal>();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "pub")));
        builder.CloseComponent();
    }
}

[Authorize(Roles = "admin")]
[OnReactiveSignal<AuthTestSignal>]
public class AdminSignalSubscriber : ReactiveComponent
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "admin-sub")));
        builder.CloseComponent();
    }
}

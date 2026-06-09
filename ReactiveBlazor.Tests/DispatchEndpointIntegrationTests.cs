using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ReactiveBlazor.Tests;

/// <summary>
/// Integration tests that exercise the full dispatch pipeline:
/// antiforgery → decrypt → rehydrate → action → render → response.
/// </summary>
public class DispatchEndpointIntegrationTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly IReactiveStateCodec _codec;
    private readonly string _antiforgeryToken;
    private readonly string _antiforgeryCookie;

    public DispatchEndpointIntegrationTests()
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
                    services.AddDataProtection()
                        .SetApplicationName("ReactiveBlazor.IntegrationTests");
                    services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
                    services.AddHttpContextAccessor();

                    var opts = new ReactiveOptions();
                    services.Configure<ReactiveOptions>(_ => { });

                    var registry = new ReactiveComponentRegistry();
                    registry.Register(typeof(IntegrationCounter));
                    registry.Register(typeof(SignalPublisher));
                    registry.Register(typeof(SignalSubscriber));
                    registry.Register(typeof(UnrelatedSibling));
                    registry.Freeze();
                    services.AddSingleton(registry);
                    services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
                    services.AddScoped<IReactiveSignals, ReactiveSignals>();
                    services.AddSingleton<IReactiveNonceStore, InMemoryReactiveNonceStore>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAntiforgery();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapReactiveComponents();
                        // Expose a helper endpoint to get the antiforgery token
                        endpoints.MapGet("/_test/antiforgery", (
                            Microsoft.AspNetCore.Antiforgery.IAntiforgery af,
                            Microsoft.AspNetCore.Http.HttpContext ctx) =>
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

        // Get antiforgery token via the helper endpoint
        var afResponse = _client.GetAsync("/_test/antiforgery").GetAwaiter().GetResult();
        _antiforgeryCookie = string.Join("; ",
            afResponse.Headers.GetValues("Set-Cookie")
                .Select(c => c.Split(';')[0]));
        var afBody = afResponse.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        _antiforgeryToken = afBody.GetProperty("token").GetString()!;

        _codec = _host.Services.GetRequiredService<IReactiveStateCodec>();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private HttpRequestMessage CreateDispatchRequest(string state, string? action, object?[]? args = null)
    {
        var id = "rtest1";
        if (!string.IsNullOrEmpty(state))
        {
            try
            {
                var (type, stateJson, _) = _codec.Unprotect(state);
                using var doc = JsonDocument.Parse(stateJson);
                id = doc.RootElement.TryGetProperty("ComponentId", out var idProp) ? idProp.GetString() ?? "rtest1" : "rtest1";
            }
            catch
            {
                // Ignore decryption errors for tampered state testing
            }
        }

        var body = new
        {
            targetId = id,
            action = action,
            args = args,
            bindings = (object?)null,
            components = new[]
            {
                new { id = id, state = state }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);
        return request;
    }

    // ── Happy-path: action mutates state and returns updated HTML ─────────

    [Fact]
    public async Task Dispatch_Increment_ReturnsUpdatedHtml()
    {
        var stateJson = """{"Count":5,"ComponentId":"rtest1"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "Increment");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The rendered HTML should contain the updated count
        Assert.Contains("data-component", html);
        Assert.Contains("data-state", html);
    }

    [Fact]
    public async Task Dispatch_ActionWithArgs_PassesArguments()
    {
        var stateJson = """{"Count":10,"ComponentId":"rtest2"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "Add", [5]);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Antiforgery validation ─────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_MissingAntiforgeryToken_Returns400()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest3"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        // Omit the antiforgery header
        var body = new { state = token, action = "Increment", args = (object?[]?)null, bindings = (object?)null };
        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Tampered state ────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_TamperedState_Returns400()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest4"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);
        var tampered = token[..^4] + "XXXX";

        var request = CreateDispatchRequest(tampered, "Increment");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Unknown action ────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_UnknownAction_Returns400()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest5"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "NonExistentAction");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Must not leak component type name
        Assert.DoesNotContain("IntegrationCounter", body);
    }

    // ── Oversized token ───────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_OversizedToken_Returns400()
    {
        // Build a state string that exceeds MaxTokenBytes (default 256 KB)
        var hugeState = new string('X', 300 * 1024);

        var request = CreateDispatchRequest(hugeState, "Increment");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Null or empty state ───────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_NullOrEmptyState_Returns400()
    {
        var request = CreateDispatchRequest(null!, "Increment");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("State token is missing.", body);
    }

    // ── Multi-component OOB updates ───────────────────────────────────────

    [Fact]
    public async Task Dispatch_MultiComponent_NoSignalPublished_OnlyTargetRendered()
    {
        var targetState = """{"Count":5,"ComponentId":"target"}""";
        var siblingState = """{"Count":10,"ComponentId":"sibling"}""";

        var targetToken = _codec.Protect(typeof(IntegrationCounter), targetState);
        var siblingToken = _codec.Protect(typeof(IntegrationCounter), siblingState);

        var body = new
        {
            targetId = "target",
            action = "Increment",
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[]
            {
                new { id = "target", state = targetToken },
                new { id = "sibling", state = siblingToken }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updates = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(updates);
        Assert.True(updates.ContainsKey("target"));
        // Sibling did not subscribe to anything and target published no signals,
        // so sibling must NOT appear in the response.
        Assert.False(updates.ContainsKey("sibling"));
    }

    [Fact]
    public async Task Dispatch_SignalPublished_RefreshesOnlySubscribedSiblings()
    {
        var targetState = """{"ComponentId":"target"}""";
        var subscribedState = """{"ComponentId":"sub"}""";
        var unrelatedState = """{"ComponentId":"unrel"}""";

        var targetToken = _codec.Protect(typeof(SignalPublisher), targetState);
        var subscribedToken = _codec.Protect(typeof(SignalSubscriber), subscribedState);
        var unrelatedToken = _codec.Protect(typeof(UnrelatedSibling), unrelatedState);

        var body = new
        {
            targetId = "target",
            action = "PublishTestSignal",
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[]
            {
                new { id = "target", state = targetToken },
                new { id = "sub", state = subscribedToken },
                new { id = "unrel", state = unrelatedToken }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updates = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(updates);
        Assert.True(updates.ContainsKey("target"));
        Assert.True(updates.ContainsKey("sub"));
        Assert.False(updates.ContainsKey("unrel"));
    }

    [Fact]
    public async Task Dispatch_NoAction_NoSignalsEmitted_OnlyTargetReturned()
    {
        // Bind-only dispatch (no action) publishes nothing, so no siblings refresh.
        var targetState = """{"Count":1,"ComponentId":"target"}""";
        var siblingState = """{"ComponentId":"sub"}""";

        var targetToken = _codec.Protect(typeof(IntegrationCounter), targetState);
        var siblingToken = _codec.Protect(typeof(SignalSubscriber), siblingState);

        var body = new
        {
            targetId = "target",
            action = (string?)null,
            args = (object?[]?)null,
            bindings = (object?)null,
            components = new[]
            {
                new { id = "target", state = targetToken },
                new { id = "sub", state = siblingToken }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updates = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(updates);
        Assert.True(updates.ContainsKey("target"));
        Assert.False(updates.ContainsKey("sub"));
    }

    [Fact]
    public async Task Dispatch_NavigationManagerRedirect_ReturnsRedirectHtmlAttribute()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest_redirect"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "RedirectViaNavigationManager");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The rendered HTML should contain data-redirect attribute pointing to our target
        Assert.Contains("data-redirect", html);
        Assert.Contains("http://localhost/target-page", html);
    }

    [Fact]
    public async Task Dispatch_OneTimeTokenAction_SucceedsFirstTime_FailsSecondTime()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest_onetime"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        // First call should succeed
        var request1 = CreateDispatchRequest(token, "ProcessCriticalAction");
        var response1 = await _client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Second call with the same token should fail due to replay prevention
        var request2 = CreateDispatchRequest(token, "ProcessCriticalAction");
        var response2 = await _client.SendAsync(request2);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);

        var errorBody = await response2.Content.ReadAsStringAsync();
        Assert.Contains("This request has already been processed.", errorBody);
    }

    // ── Authorization failures map to 403 ──────────────────────────────────

    [Fact]
    public async Task Dispatch_ActionThrowsUnauthorized_Returns403()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest_deny"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "Deny");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Unexpected action exceptions return a sanitized 500 (no leak) ───────

    [Fact]
    public async Task Dispatch_ActionThrowsUnexpected_Returns500_WithoutLeakingDetails()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest_boom"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var request = CreateDispatchRequest(token, "Boom");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super secret internal detail", body);
        Assert.DoesNotContain("InvalidProgramException", body);
    }

    // ── Malformed client binding input is a 400, not a 500 ─────────────────

    [Fact]
    public async Task Dispatch_InvalidBindingValue_Returns400()
    {
        var stateJson = """{"Count":0,"ComponentId":"rtest_bind"}""";
        var token = _codec.Protect(typeof(IntegrationCounter), stateJson);

        var body = new
        {
            targetId = "rtest_bind",
            action = (string?)null,
            args = (object?[]?)null,
            bindings = new Dictionary<string, object?> { ["Count"] = "not-a-number" },
            components = new[] { new { id = "rtest_bind", state = token } }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Too many components rejected before decryption (DoS guard) ──────────

    [Fact]
    public async Task Dispatch_TooManyComponents_Returns400()
    {
        // Default MaxComponentsPerDispatch is 100; send 101 dummy components.
        // The guard runs before decryption, so the state values need not be valid.
        var components = Enumerable.Range(0, 101)
            .Select(i => new { id = $"c{i}", state = "x" })
            .ToArray();

        var body = new
        {
            targetId = "c0",
            action = (string?)null,
            args = (object?[]?)null,
            bindings = (object?)null,
            components
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/_reactive/dispatch")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        request.Headers.Add("Cookie", _antiforgeryCookie);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("Too many components", errorBody);
    }
}

// ── Test component ──────────────────────────────────────────────────────────

/// <summary>
/// Minimal reactive component used exclusively for integration tests.
/// </summary>
public class IntegrationCounter : ReactiveComponent
{
    public int Count { get; set; }

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [ReactiveAction]
    public void Increment() => Count++;

    [ReactiveAction]
    public void Add(int amount) => Count += amount;

    [ReactiveAction]
    public void RedirectViaNavigationManager()
    {
        NavigationManager.NavigateTo("http://localhost/target-page");
    }

    [ReactiveAction(RequireOneTimeToken = true)]
    public void ProcessCriticalAction()
    {
        Count += 10;
    }

    [ReactiveAction]
    public void Deny() => throw new UnauthorizedAccessException("nope");

    [ReactiveAction]
    public void Boom() => throw new InvalidProgramException("super secret internal detail");

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(childBuilder =>
        {
            childBuilder.AddContent(3, $"Count: {Count}");
        }));
        builder.CloseComponent();
    }
}

// ── Signal test components ──────────────────────────────────────────────

public sealed record IntegrationTestSignal : IReactiveSignal;

public class SignalPublisher : ReactiveComponent
{
    [ReactiveAction]
    public void PublishTestSignal() => ReactiveSignals.Publish<IntegrationTestSignal>();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "publisher")));
        builder.CloseComponent();
    }
}

[OnReactiveSignal<IntegrationTestSignal>]
public class SignalSubscriber : ReactiveComponent
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "subscriber")));
        builder.CloseComponent();
    }
}

public class UnrelatedSibling : ReactiveComponent
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(cb => cb.AddContent(3, "unrelated")));
        builder.CloseComponent();
    }
}

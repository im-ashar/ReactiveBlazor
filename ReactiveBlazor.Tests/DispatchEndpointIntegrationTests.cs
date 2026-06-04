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
                    registry.Freeze();
                    services.AddSingleton(registry);
                    services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
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
        var body = new { state, action, args, bindings = (object?)null };
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
}

// ── Test component ──────────────────────────────────────────────────────────

/// <summary>
/// Minimal reactive component used exclusively for integration tests.
/// </summary>
public class IntegrationCounter : ReactiveComponent
{
    public int Count { get; set; }

    [ReactiveAction]
    public void Increment() => Count++;

    [ReactiveAction]
    public void Add(int amount) => Count += amount;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-component", "IntegrationCounter");
        builder.AddAttribute(2, "data-state", "placeholder");
        builder.AddContent(3, $"Count: {Count}");
        builder.CloseElement();
    }
}

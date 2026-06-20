using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReactiveBlazor.Tests;

/// <summary>
/// Verifies that <see cref="ReactiveRoot"/> emits the polling data-attributes only when
/// polling is enabled (PollAction set and PollInterval &gt; 0), and omits them otherwise.
/// </summary>
public class ReactiveRootPollingTests
{
    private static async Task<string> RenderAsync(string? pollAction, int pollInterval, string? pollArgs)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("ReactiveBlazor.PollingTests");
        services.Configure<ReactiveOptions>(_ => { });
        services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
        services.AddScoped<IReactiveSignals, ReactiveSignals>();
        services.AddSingleton<NavigationManager, TestNavigationManager>();

        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(PollingTestComponent));
        registry.Freeze();
        services.AddSingleton(registry);

        var sp = services.BuildServiceProvider();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        await using var renderer = new HtmlRenderer(sp, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PollingTestComponent.PollAction)] = pollAction,
                [nameof(PollingTestComponent.PollInterval)] = pollInterval,
                [nameof(PollingTestComponent.PollArgs)] = pollArgs,
            });
            var output = await renderer.RenderComponentAsync<PollingTestComponent>(parameters);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task PollEnabled_EmitsPollAttributes()
    {
        var html = await RenderAsync("Sample", 2000, null);

        Assert.Contains("data-poll=\"Sample\"", html);
        Assert.Contains("data-poll-interval=\"2000\"", html);
    }

    [Fact]
    public async Task PollIntervalZero_OmitsPollAttributes()
    {
        var html = await RenderAsync("Sample", 0, null);

        Assert.DoesNotContain("data-poll", html);
    }

    [Fact]
    public async Task PollActionMissing_OmitsPollAttributes()
    {
        var html = await RenderAsync(null, 2000, null);

        Assert.DoesNotContain("data-poll", html);
    }

    [Fact]
    public async Task PollArgsSet_EmitsPollArgsAttribute()
    {
        var html = await RenderAsync("Sample", 2000, "[1,\"x\"]");

        Assert.Contains("data-poll-args=", html);
        Assert.Contains("[1,", html);
    }

    [Fact]
    public async Task PollArgsUnset_OmitsPollArgsAttribute()
    {
        var html = await RenderAsync("Sample", 2000, null);

        Assert.Contains("data-poll=\"Sample\"", html);
        Assert.DoesNotContain("data-poll-args", html);
    }
}

/// <summary>Test component that forwards poll parameters to its <see cref="ReactiveRoot"/>.</summary>
public class PollingTestComponent : ReactiveComponent
{
    [Parameter] public string? PollAction { get; set; }
    [Parameter] public int PollInterval { get; set; }
    [Parameter] public string? PollArgs { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ReactiveRoot>(0);
        builder.AddAttribute(1, "Owner", this);
        builder.AddAttribute(2, nameof(ReactiveRoot.PollAction), PollAction);
        builder.AddAttribute(3, nameof(ReactiveRoot.PollInterval), PollInterval);
        builder.AddAttribute(4, nameof(ReactiveRoot.PollArgs), PollArgs);
        builder.AddAttribute(5, "ChildContent", (RenderFragment)(cb => cb.AddContent(6, "poll-test")));
        builder.CloseComponent();
    }
}

/// <summary>Minimal NavigationManager so ReactiveRoot's [Inject] NavigationManager resolves.</summary>
internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    protected override void NavigateToCore(string uri, bool forceLoad) { }
}

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ReactiveBlazor.Tests;

// ---------------------------------------------------------------------------
// Minimal fake components used across all tests
// ---------------------------------------------------------------------------

public class CounterComponent : ReactiveComponent
{
    public int Count { get; set; }
    public string Label { get; set; } = "clicks";

    [ReactiveIgnore]
    public string Derived { get; set; } = "ignored";

    [Parameter]
    public string? Title { get; set; }

    [ReactiveAction]
    public void Increment() => Count++;

    [ReactiveAction]
    public void Add(int amount) => Count += amount;

    [ReactiveAction]
    public async Task IncrementAsync()
    {
        await Task.Yield();
        Count++;
    }

    // NOT decorated — must not be callable remotely
    public void NotAnAction() => Count = 999;

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
}

public class OtherComponent : ReactiveComponent
{
    public string Name { get; set; } = "other";
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

internal static class TestServices
{
    /// <summary>
    /// Builds a minimal IServiceProvider with Data Protection, a frozen registry
    /// containing only the supplied component types, and the given options.
    /// </summary>
    public static (IServiceProvider Services, ReactiveStateCodec Codec) Build(
        Action<ReactiveOptions>? configure = null,
        params Type[] componentTypes)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();

        var opts = new ReactiveOptions();
        configure?.Invoke(opts);
        services.AddSingleton(Options.Create(opts));

        var registry = new ReactiveComponentRegistry();
        foreach (var t in componentTypes.DefaultIfEmpty(typeof(CounterComponent)))
            registry.Register(t);
        registry.Freeze();
        services.AddSingleton(registry);
        services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();

        var sp = services.BuildServiceProvider();
        var codec = (ReactiveStateCodec)sp.GetRequiredService<IReactiveStateCodec>();
        return (sp, codec);
    }
}

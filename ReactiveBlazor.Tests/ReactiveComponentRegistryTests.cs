namespace ReactiveBlazor.Tests;

/// <summary>
/// Tests for <see cref="ReactiveComponentRegistry"/> — registration, freeze enforcement,
/// lookup by key and type, and rejection of unknown keys.
/// </summary>
public class ReactiveComponentRegistryTests
{
    [Fact]
    public void Register_AddsComponent_LookupByTypeSucceeds()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(CounterComponent));

        var key = registry.GetKey(typeof(CounterComponent));
        Assert.Equal(typeof(CounterComponent).FullName, key);
    }

    [Fact]
    public void Register_AddsComponent_LookupByKeySucceeds()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(CounterComponent));

        var type = registry.GetType(typeof(CounterComponent).FullName!);
        Assert.Equal(typeof(CounterComponent), type);
    }

    [Fact]
    public void RegisterAssembly_ScansAndRegistersAllReactiveComponents()
    {
        var registry = new ReactiveComponentRegistry();
        registry.RegisterAssembly(typeof(CounterComponent).Assembly);

        // Both CounterComponent and OtherComponent live in this assembly
        Assert.Equal(typeof(CounterComponent), registry.GetType(typeof(CounterComponent).FullName!));
        Assert.Equal(typeof(OtherComponent), registry.GetType(typeof(OtherComponent).FullName!));
    }

    [Fact]
    public void GetType_UnknownKey_ThrowsInvalidOperationException()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Freeze();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetType("DoesNotExist"));
        Assert.Contains("DoesNotExist", ex.Message);
    }

    [Fact]
    public void GetKey_UnregisteredType_ThrowsInvalidOperationException()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Freeze();

        Assert.Throws<InvalidOperationException>(() => registry.GetKey(typeof(CounterComponent)));
    }

    [Fact]
    public void Freeze_PreventsSubsequentRegistration()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Freeze();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(CounterComponent)));
        Assert.Contains("frozen", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Freeze_IsIdempotent()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(CounterComponent));
        registry.Freeze();
        registry.Freeze(); // should not throw

        Assert.Equal(typeof(CounterComponent), registry.GetType(typeof(CounterComponent).FullName!));
    }

    [Fact]
    public void ConcurrentReads_AfterFreeze_AreThreadSafe()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(CounterComponent));
        registry.Register(typeof(OtherComponent));
        registry.Freeze();

        var key = typeof(CounterComponent).FullName!;

        // Hammer concurrent lookups — should never throw or return wrong result
        Parallel.For(0, 500, _ =>
        {
            var t = registry.GetType(key);
            Assert.Equal(typeof(CounterComponent), t);
        });
    }
}

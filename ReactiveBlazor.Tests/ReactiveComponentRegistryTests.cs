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

    // ── Signal subscriptions ──────────────────────────────────────────────

    [Fact]
    public void GetSubscribers_UnknownSignalType_ReturnsEmpty()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Freeze();

        Assert.Empty(registry.GetSubscribers(typeof(TestSignalAlpha)));
    }

    [Fact]
    public void Register_RecordsGenericAttributeSubscription()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(SubscribesToAlpha));
        registry.Freeze();

        Assert.Contains(typeof(SubscribesToAlpha), registry.GetSubscribers(typeof(TestSignalAlpha)));
    }

    [Fact]
    public void Register_StackedAttributes_AllRecorded()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(SubscribesToAlphaAndBeta));
        registry.Freeze();

        Assert.Contains(typeof(SubscribesToAlphaAndBeta), registry.GetSubscribers(typeof(TestSignalAlpha)));
        Assert.Contains(typeof(SubscribesToAlphaAndBeta), registry.GetSubscribers(typeof(TestSignalBeta)));
    }

    [Fact]
    public void Register_NonGenericAttribute_Recorded()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(SubscribesToGammaNonGeneric));
        registry.Freeze();

        Assert.Contains(typeof(SubscribesToGammaNonGeneric), registry.GetSubscribers(typeof(TestSignalGamma)));
    }

    [Fact]
    public void Register_InheritedAttribute_AppliesToDerivedComponent()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(DerivedFromAlphaSubscriber));
        registry.Freeze();

        Assert.Contains(typeof(DerivedFromAlphaSubscriber), registry.GetSubscribers(typeof(TestSignalAlpha)));
    }

    [Fact]
    public void Register_MultipleComponentsForSameSignal_AllReturned()
    {
        var registry = new ReactiveComponentRegistry();
        registry.Register(typeof(SubscribesToAlpha));
        registry.Register(typeof(SubscribesToAlphaAndBeta));
        registry.Freeze();

        var subs = registry.GetSubscribers(typeof(TestSignalAlpha));
        Assert.Contains(typeof(SubscribesToAlpha), subs);
        Assert.Contains(typeof(SubscribesToAlphaAndBeta), subs);
    }

    [Fact]
    public void OnReactiveSignalAttribute_NonGeneric_RejectsNonSignalType()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OnReactiveSignalAttribute(typeof(string)));
        Assert.Contains(nameof(IReactiveSignal), ex.Message);
    }
}

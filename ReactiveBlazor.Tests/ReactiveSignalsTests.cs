namespace ReactiveBlazor.Tests;

public class ReactiveSignalsTests
{
    public sealed record SignalA : IReactiveSignal;
    public sealed record SignalB : IReactiveSignal;
    public sealed record SignalWithPayload(int Value) : IReactiveSignal;

    private sealed class NotASignal { }

    [Fact]
    public void Publish_Generic_NewT_RecordsTypeAndPayload()
    {
        var signals = new ReactiveSignals();

        signals.Publish<SignalA>();

        Assert.Contains(typeof(SignalA), signals.PublishedTypes);
        var published = Assert.Single(signals.Published);
        Assert.Equal(typeof(SignalA), published.Type);
        Assert.NotNull(published.Payload);
        Assert.IsType<SignalA>(published.Payload);
    }

    [Fact]
    public void Publish_Generic_Instance_StoresInstanceAsPayload()
    {
        var signals = new ReactiveSignals();
        var payload = new SignalWithPayload(42);

        signals.Publish(payload);

        Assert.Contains(typeof(SignalWithPayload), signals.PublishedTypes);
        var published = Assert.Single(signals.Published);
        Assert.Same(payload, published.Payload);
    }

    [Fact]
    public void Publish_Generic_Instance_NullSignal_Throws()
    {
        var signals = new ReactiveSignals();

        Assert.Throws<ArgumentNullException>(() => signals.Publish<SignalA>(null!));
    }

    [Fact]
    public void Publish_RuntimeType_StoresType()
    {
        var signals = new ReactiveSignals();

        signals.Publish(typeof(SignalA));

        Assert.Contains(typeof(SignalA), signals.PublishedTypes);
    }

    [Fact]
    public void Publish_RuntimeType_NotASignal_Throws()
    {
        var signals = new ReactiveSignals();

        var ex = Assert.Throws<ArgumentException>(() => signals.Publish(typeof(NotASignal)));
        Assert.Contains(nameof(IReactiveSignal), ex.Message);
    }

    [Fact]
    public void Publish_RuntimeType_NullType_Throws()
    {
        var signals = new ReactiveSignals();

        Assert.Throws<ArgumentNullException>(() => signals.Publish(null!));
    }

    [Fact]
    public void Publish_DuplicateTypes_PublishedTypesContainsTypeOnce()
    {
        var signals = new ReactiveSignals();

        signals.Publish<SignalA>();
        signals.Publish<SignalA>();
        signals.Publish<SignalB>();

        Assert.Equal(2, signals.PublishedTypes.Count);
        Assert.Equal(3, signals.Published.Count);
    }

    [Fact]
    public void TwoInstances_AreIndependent()
    {
        var a = new ReactiveSignals();
        var b = new ReactiveSignals();

        a.Publish<SignalA>();

        Assert.Contains(typeof(SignalA), a.PublishedTypes);
        Assert.Empty(b.PublishedTypes);
    }

    // ── Query methods (GetPublished / WasPublished) ───────────────────────

    [Fact]
    public void GetPublished_ReturnsPayloads_InPublishOrder()
    {
        var signals = new ReactiveSignals();
        signals.Publish(new SignalWithPayload(1));
        signals.Publish<SignalA>();
        signals.Publish(new SignalWithPayload(2));

        var published = signals.GetPublished<SignalWithPayload>().ToList();

        Assert.Equal(2, published.Count);
        Assert.Equal(1, published[0].Value);
        Assert.Equal(2, published[1].Value);
    }

    [Fact]
    public void GetPublished_NoMatches_ReturnsEmpty()
    {
        var signals = new ReactiveSignals();
        signals.Publish<SignalA>();

        Assert.Empty(signals.GetPublished<SignalB>());
    }

    [Fact]
    public void GetPublished_DoesNotReturnPayloadlessRuntimePublishes()
    {
        var signals = new ReactiveSignals();
        // Publishing by runtime Type with a null payload makes the *type* signaled
        // but there's no instance to return from GetPublished<T>().
        signals.Publish(typeof(SignalA), payload: null);

        Assert.Empty(signals.GetPublished<SignalA>());
        // But the type was still recorded as published.
        Assert.True(signals.WasPublished<SignalA>());
    }

    [Fact]
    public void WasPublished_Generic_TrueWhenPublished()
    {
        var signals = new ReactiveSignals();
        signals.Publish<SignalA>();

        Assert.True(signals.WasPublished<SignalA>());
        Assert.False(signals.WasPublished<SignalB>());
    }

    [Fact]
    public void WasPublished_NonGeneric_TrueWhenPublished()
    {
        var signals = new ReactiveSignals();
        signals.Publish<SignalA>();

        Assert.True(signals.WasPublished(typeof(SignalA)));
        Assert.False(signals.WasPublished(typeof(SignalB)));
    }

    [Fact]
    public void WasPublished_NullType_Throws()
    {
        var signals = new ReactiveSignals();
        Assert.Throws<ArgumentNullException>(() => signals.WasPublished(null!));
    }

    public interface IGroupSignal : IReactiveSignal;
    public sealed record GroupedA : IGroupSignal;
    public sealed record GroupedB : IGroupSignal;

    [Fact]
    public void WasPublished_PolymorphicQueryByBaseInterface_MatchesAnyDerived()
    {
        var signals = new ReactiveSignals();
        signals.Publish<GroupedA>();

        Assert.True(signals.WasPublished<IGroupSignal>());
        Assert.True(signals.WasPublished(typeof(IGroupSignal)));
    }

    [Fact]
    public void GetPublished_PolymorphicQueryByBaseInterface_ReturnsAllAssignable()
    {
        var signals = new ReactiveSignals();
        signals.Publish<GroupedA>();
        signals.Publish<GroupedB>();
        signals.Publish<SignalA>();

        var grouped = signals.GetPublished<IGroupSignal>().ToList();
        Assert.Equal(2, grouped.Count);
    }
}

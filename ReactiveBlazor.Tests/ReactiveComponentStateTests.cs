using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ReactiveBlazor.Tests;

/// <summary>
/// Tests for <see cref="ReactiveComponent"/> — state property discovery,
/// serialization/deserialization, action dispatch, and binding application.
/// </summary>
public class ReactiveComponentStateTests
{
    // ── StateProperties ───────────────────────────────────────────────────────

    [Fact]
    public void StateProperties_IncludesPublicReadWriteProperties_DeclaredOnSubclass()
    {
        var props = ReactiveComponent.StateProperties(typeof(CounterComponent));
        var names = props.Select(p => p.Name).ToHashSet();

        // DeclaredOnly — these are on CounterComponent itself
        Assert.Contains("Count", names);
        Assert.Contains("Label", names);
    }

    [Fact]
    public void StateProperties_ExcludesReactiveIgnoreProperties()
    {
        var props = ReactiveComponent.StateProperties(typeof(CounterComponent));
        var names = props.Select(p => p.Name).ToHashSet();

        Assert.DoesNotContain("Derived", names);
    }

    [Fact]
    public void StateProperties_ExcludesParameterProperties()
    {
        var props = ReactiveComponent.StateProperties(typeof(CounterComponent));
        var names = props.Select(p => p.Name).ToHashSet();

        // [Parameter] Title must not be in state
        Assert.DoesNotContain("Title", names);
    }

    [Fact]
    public void StateProperties_ExcludesReactiveIgnoreOnBaseClass()
    {
        // RedirectUrl has [ReactiveIgnore] on the base class
        var props = ReactiveComponent.StateProperties(typeof(CounterComponent));
        var names = props.Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("RedirectUrl", names);
    }

    [Fact]
    public void StateProperties_ResultIsCached_ReturnsSameArray()
    {
        var a = ReactiveComponent.StateProperties(typeof(CounterComponent));
        var b = ReactiveComponent.StateProperties(typeof(CounterComponent));
        Assert.Same(a, b);
    }

    [Fact]
    public void StateProperties_DifferentTypes_AreIndependent()
    {
        var counterProps = ReactiveComponent.StateProperties(typeof(CounterComponent))
            .Select(p => p.Name).ToHashSet();
        var otherProps = ReactiveComponent.StateProperties(typeof(OtherComponent))
            .Select(p => p.Name).ToHashSet();

        Assert.Contains("Count", counterProps);
        Assert.Contains("Name", otherProps);
        Assert.DoesNotContain("Name", counterProps);
        Assert.DoesNotContain("Count", otherProps);
    }

    // ── SerializeState ────────────────────────────────────────────────────────

    [Fact]
    public void SerializeState_IncludesAllDeclaredStateProperties()
    {
        var comp = new CounterComponent { Count = 7, Label = "taps" };
        comp.ComponentId = "rid123";

        var json = InvokeSerializeState(comp);

        // PascalCase property names (Web JSON = camelCase on KEYS when using JsonSerializerDefaults.Web,
        // but we serialize a Dictionary<string, object?> keyed by property name = PascalCase)
        Assert.Contains("Count", json);
        Assert.Contains("Label", json);
        Assert.Contains("ComponentId", json);
        Assert.Contains("7", json);
        Assert.Contains("taps", json);
    }

    [Fact]
    public void SerializeState_ExcludesIgnoredProperties()
    {
        var comp = new CounterComponent { Derived = "should-not-appear" };

        var json = InvokeSerializeState(comp);

        Assert.DoesNotContain("Derived", json);
        Assert.DoesNotContain("should-not-appear", json);
    }

    [Fact]
    public void SerializeState_IsValidJson()
    {
        var comp = new CounterComponent { Count = 3, Label = "clicks" };
        var json = InvokeSerializeState(comp);

        // Should not throw
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void SerializeState_RoundTrips_ViaApplyState()
    {
        var comp1 = new CounterComponent { Count = 55, Label = "original" };
        comp1.ComponentId = "rstable";

        var json = InvokeSerializeState(comp1);

        var comp2 = new CounterComponent();
        InvokeApplyState(comp2, json);

        Assert.Equal(55, comp2.Count);
        Assert.Equal("original", comp2.Label);
        Assert.Equal("rstable", comp2.ComponentId);
    }

    // ── ApplyState ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyState_RestoresPropertiesFromJson()
    {
        var comp = new CounterComponent();
        // Use PascalCase keys since that's what SerializeState produces
        InvokeApplyState(comp, """{"Count":10,"Label":"restored","ComponentId":"rxyz"}""");

        Assert.Equal(10, comp.Count);
        Assert.Equal("restored", comp.Label);
        Assert.Equal("rxyz", comp.ComponentId);
    }

    [Fact]
    public void ApplyState_EmptyJson_LeavesDefaultValues()
    {
        var comp = new CounterComponent { Count = 5, Label = "before" };
        InvokeApplyState(comp, "{}");

        // Properties not in the JSON remain unchanged
        Assert.Equal(5, comp.Count);
        Assert.Equal("before", comp.Label);
    }

    [Fact]
    public void ApplyState_IgnoresUnknownKeys()
    {
        var comp = new CounterComponent { Count = 1 };

        // Should not throw on unknown property
        InvokeApplyState(comp, """{"Count":2,"UnknownProp":"x"}""");
        Assert.Equal(2, comp.Count);
    }

    // ── ApplyBindings ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyBindings_OverridesPropertyValue()
    {
        var comp = new CounterComponent { Label = "original" };
        InvokeApplyBindings(comp, """{"Label":"updated"}""");
        Assert.Equal("updated", comp.Label);
    }

    [Fact]
    public void ApplyBindings_CoercesNumericString_ToInt()
    {
        var comp = new CounterComponent { Count = 0 };
        InvokeApplyBindings(comp, """{"Count":"42"}""");
        Assert.Equal(42, comp.Count);
    }

    [Fact]
    public void ApplyBindings_EmptyJson_LeavesValuesUnchanged()
    {
        var comp = new CounterComponent { Count = 7, Label = "x" };
        InvokeApplyBindings(comp, "{}");
        Assert.Equal(7, comp.Count);
        Assert.Equal("x", comp.Label);
    }

    [Fact]
    public void ApplyBindings_BoolString_CoercedCorrectly()
    {
        var comp = new BoolComponent();
        InvokeApplyBindings(comp, """{"IsEnabled":"true"}""");
        Assert.True(comp.IsEnabled);

        InvokeApplyBindings(comp, """{"IsEnabled":"false"}""");
        Assert.False(comp.IsEnabled);
    }

    // ── Action resolution ─────────────────────────────────────────────────────

    [Fact]
    public void InvokeAction_SyncNoArgs_MutatesState()
    {
        var comp = new CounterComponent { Count = 5 };
        comp.ComponentId = "r1";
        InvokeAction(comp, "Increment", null);
        Assert.Equal(6, comp.Count);
    }

    [Fact]
    public void InvokeAction_SyncWithArgs_PassesArguments()
    {
        var comp = new CounterComponent { Count = 5 };
        comp.ComponentId = "r1";
        InvokeAction(comp, "Add", "[10]");
        Assert.Equal(15, comp.Count);
    }

    [Fact]
    public async Task InvokeAction_AsyncNoArgs_Awaited()
    {
        var comp = new CounterComponent { Count = 0 };
        comp.ComponentId = "r1";
        await InvokeActionAsync(comp, "IncrementAsync", null);
        Assert.Equal(1, comp.Count);
    }

    [Fact]
    public void InvokeAction_NonDecoratedMethod_ThrowsInvalidOperationException()
    {
        var comp = new CounterComponent();
        comp.ComponentId = "r1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeAction(comp, "NotAnAction", null));

        Assert.Contains("Unknown action", ex.Message);
        // Must NOT leak the type name
        Assert.DoesNotContain("CounterComponent", ex.Message);
    }

    [Fact]
    public void InvokeAction_UnknownMethod_ThrowsInvalidOperationException()
    {
        var comp = new CounterComponent();

        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeAction(comp, "MethodThatDoesNotExist", null));

        Assert.Contains("Unknown action", ex.Message);
    }

    [Fact]
    public void InvokeAction_ConstructorInjection_IsNotCallable()
    {
        // Makes sure special-named members like property getters can't be invoked
        var comp = new CounterComponent();
        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeAction(comp, "get_Count", null));

        Assert.Contains("Unknown action", ex.Message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string InvokeSerializeState(ReactiveComponent comp)
    {
        var method = typeof(ReactiveComponent)
            .GetMethod("SerializeState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (string)method.Invoke(comp, null)!;
    }

    private static void InvokeApplyState(ReactiveComponent comp, string json)
    {
        var method = typeof(ReactiveComponent)
            .GetMethod("ApplyState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(comp, [json]);
    }

    private static void InvokeApplyBindings(ReactiveComponent comp, string json)
    {
        var method = typeof(ReactiveComponent)
            .GetMethod("ApplyBindings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(comp, [json]);
    }

    private static void InvokeAction(ReactiveComponent comp, string action, string? argsJson)
    {
        var method = typeof(ReactiveComponent)
            .GetMethod("InvokeActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // InvokeActionAsync returns a Task — get it and .GetAwaiter().GetResult()
        var task = (Task)method.Invoke(comp, [action, argsJson])!;
        task.GetAwaiter().GetResult();
    }

    private static async Task InvokeActionAsync(ReactiveComponent comp, string action, string? argsJson)
    {
        var method = typeof(ReactiveComponent)
            .GetMethod("InvokeActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(comp, [action, argsJson])!;
        await task;
    }
}

// Extra helper component for bool binding test
public class BoolComponent : ReactiveComponent
{
    public bool IsEnabled { get; set; }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
}

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ReactiveBlazor;

/// <summary>
/// Base class for stateful SSR components. Inherit from this with `@inherits ReactiveBlazor.ReactiveComponent`
/// and wrap your markup in &lt;ReactiveRoot Owner="this"&gt;.
///
/// Public read/write properties declared on your component are treated as STATE: they are serialized into
/// the page (signed) and rehydrated on every round-trip. Public methods declared on your component are
/// treated as ACTIONS that the client can invoke by name.
/// </summary>
public abstract class ReactiveComponent : ComponentBase
{
    // ---- Internal control parameters. Set ONLY by the dispatch endpoint, never from your markup. ----
    [Parameter] public string? ReactiveState { get; set; }
    [Parameter] public string? ReactiveAction { get; set; }
    [Parameter] public string? ReactiveArgs { get; set; }
    [Parameter] public string? ReactiveBindings { get; set; }

    /// <summary>Stable identity carried across round-trips inside the signed state (used by the morph step).</summary>
    public string ComponentId { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        // 1. Apply the control parameters (and any real [Parameter]s) coming in.
        parameters.SetParameterProperties(this);

        // 2. Rehydrate prior state (no-op on the very first page render).
        if (!string.IsNullOrEmpty(ReactiveState))
            ApplyState(ReactiveState);

        if (string.IsNullOrEmpty(ComponentId))
            ComponentId = "r" + Guid.NewGuid().ToString("N")[..12];

        // 3. Apply two-way bound input values the client sent.
        if (!string.IsNullOrEmpty(ReactiveBindings))
            ApplyBindings(ReactiveBindings);

        // 4. Run the requested action (mutates state).
        if (!string.IsNullOrEmpty(ReactiveAction))
            await InvokeActionAsync(ReactiveAction, ReactiveArgs);

        // 5. Trigger the normal lifecycle (OnInitialized / OnParametersSet) and render.
        await base.SetParametersAsync(ParameterView.Empty);
    }

    // ---- State serialization (used by ReactiveRoot) ----

    internal string SerializeState()
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in StateProperties(GetType()))
            dict[p.Name] = p.GetValue(this);
        dict[nameof(ComponentId)] = ComponentId;
        return JsonSerializer.Serialize(dict, Json);
    }

    private void ApplyState(string json)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json);
        if (doc is null) return;
        foreach (var p in StateProperties(GetType()))
            if (doc.TryGetValue(p.Name, out var val))
                p.SetValue(this, val.Deserialize(p.PropertyType, Json));
        if (doc.TryGetValue(nameof(ComponentId), out var id))
            ComponentId = id.GetString() ?? ComponentId;
    }

    private void ApplyBindings(string json)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json);
        if (doc is null) return;
        foreach (var p in StateProperties(GetType()))
            if (doc.TryGetValue(p.Name, out var val))
                p.SetValue(this, ConvertBinding(val, p.PropertyType));
    }

    // ---- Action dispatch ----

    private async Task InvokeActionAsync(string action, string? argsJson)
    {
        // SAFETY: only public instance methods *declared on a ReactiveComponent subclass* (i.e. your own
        // component) are callable. Framework / object / lifecycle methods are excluded. Harden further by
        // requiring an explicit [ReactiveAction] attribute if you prefer opt-in.
        var method = GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == action &&
                typeof(ReactiveComponent).IsAssignableFrom(m.DeclaringType) &&
                m.DeclaringType != typeof(ReactiveComponent) &&
                !m.IsSpecialName);

        if (method is null)
            throw new InvalidOperationException($"'{action}' is not a callable action on {GetType().Name}.");

        var args = BindArgs(method, argsJson);
        var result = method.Invoke(this, args);
        if (result is Task task) await task;
    }

    // ---- Helpers ----

    private static IEnumerable<PropertyInfo> StateProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.CanRead && p.CanWrite
            && typeof(ReactiveComponent).IsAssignableFrom(p.DeclaringType)
            && p.DeclaringType != typeof(ReactiveComponent)
            && p.GetCustomAttribute<ParameterAttribute>() is null
            && p.GetCustomAttribute<CascadingParameterAttribute>() is null
            && p.GetCustomAttribute<InjectAttribute>() is null);

    private static object? ConvertBinding(JsonElement el, Type target)
    {
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        if (s is null) return null;
        var nt = Nullable.GetUnderlyingType(target) ?? target;
        if (nt == typeof(string)) return s;
        if (nt == typeof(bool)) return s is "true" or "True" or "on";
        if (nt.IsPrimitive || nt == typeof(decimal)) return Convert.ChangeType(s, nt);
        return JsonSerializer.Deserialize(s, target, Json);
    }

    private static object?[] BindArgs(MethodInfo method, string? argsJson)
    {
        var ps = method.GetParameters();
        if (ps.Length == 0) return Array.Empty<object?>();
        var arr = string.IsNullOrEmpty(argsJson)
            ? Array.Empty<JsonElement>()
            : JsonSerializer.Deserialize<JsonElement[]>(argsJson, Json) ?? Array.Empty<JsonElement>();
        var result = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
            result[i] = i < arr.Length
                ? arr[i].Deserialize(ps[i].ParameterType, Json)
                : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
        return result;
    }
}

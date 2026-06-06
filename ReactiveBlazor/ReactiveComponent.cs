using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ReactiveBlazor;

/// <summary>
/// Base class for stateful SSR components that support client-side interactivity without
/// SignalR or WebAssembly. Inherit from this and wrap your markup in
/// <c>&lt;ReactiveRoot Owner="this"&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>State</b>: Public read/write properties declared on your subclass are automatically
/// serialized into the page (signed and encrypted) and rehydrated on every round-trip.
/// Exclude properties with <see cref="ReactiveIgnoreAttribute"/>.
/// </para>
/// <para>
/// <b>Actions</b>: Public methods decorated with <see cref="ReactiveActionAttribute"/> can
/// be invoked from the client via <c>data-on-click</c>, <c>data-on-change</c>, etc.
/// </para>
/// </remarks>
public abstract class ReactiveComponent : ComponentBase
{
    // ---- Internal control parameters. Set by the dispatch endpoint, never from user markup. ----

    /// <exclude />
    [Parameter, EditorBrowsable(EditorBrowsableState.Never)]
    public string? ReactiveState { get; set; }

    /// <exclude />
    [Parameter, EditorBrowsable(EditorBrowsableState.Never)]
    public string? ReactiveAction { get; set; }

    /// <exclude />
    [Parameter, EditorBrowsable(EditorBrowsableState.Never)]
    public string? ReactiveArgs { get; set; }

    /// <exclude />
    [Parameter, EditorBrowsable(EditorBrowsableState.Never)]
    public string? ReactiveBindings { get; set; }

    /// <summary>
    /// Stable identity carried across round-trips inside the signed state.
    /// Used by the client-side morph step to locate the component boundary.
    /// </summary>
    public string ComponentId { get; set; } = "";

    [Inject]
    private Microsoft.Extensions.Options.IOptions<ReactiveOptions>? Options { get; set; }

    /// <summary>
    /// Per-request bus for publishing reactive signals. Subscribed sibling components
    /// (decorated with <c>[OnReactiveSignal&lt;T&gt;]</c>) are re-rendered out-of-band in the
    /// same dispatch response.
    /// </summary>
    /// <example>
    /// <code>
    /// [ReactiveAction]
    /// public void AddToCart(int productId)
    /// {
    ///     _cart.Add(productId);
    ///     ReactiveSignals.Publish&lt;CartChanged&gt;();
    /// }
    /// </code>
    /// </example>
    [Inject]
    protected IReactiveSignals ReactiveSignals { get; set; } = default!;

    private bool RequireOptIn => Options?.Value?.RequireOptInState ?? false;

    /// <summary>
    /// When set to a URL by an action method, the client will navigate to that URL
    /// after the response is received instead of morphing the DOM.
    /// </summary>
    [ReactiveIgnore]
    public string? RedirectUrl { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        // 1. Apply control parameters (and any real [Parameter]s) coming in.
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
        foreach (var p in StateProperties(GetType(), RequireOptIn))
            dict[p.Name] = p.GetValue(this);
        dict[nameof(ComponentId)] = ComponentId;
        return JsonSerializer.Serialize(dict, Json);
    }

    private void ApplyState(string json)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json);
        if (doc is null) return;
        foreach (var p in StateProperties(GetType(), RequireOptIn))
            if (doc.TryGetValue(p.Name, out var val))
                p.SetValue(this, val.Deserialize(p.PropertyType, Json));
        if (doc.TryGetValue(nameof(ComponentId), out var id))
            ComponentId = id.GetString() ?? ComponentId;
    }

    private void ApplyBindings(string json)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json);
        if (doc is null) return;
        var caseInsensitiveDoc = new Dictionary<string, JsonElement>(doc, StringComparer.OrdinalIgnoreCase);
        foreach (var p in StateProperties(GetType(), RequireOptIn))
            if (caseInsensitiveDoc.TryGetValue(p.Name, out var val))
                p.SetValue(this, ConvertBinding(val, p.PropertyType));
    }

    // ---- Action dispatch ----

    private async Task InvokeActionAsync(string action, string? argsJson)
    {
        var method = ResolveAction(GetType(), action)
            ?? throw new InvalidOperationException(
                $"Unknown action '{action}'. " +
                "Ensure the method is public, declared on your component, and decorated with [ReactiveAction].");

        var args = BindArgs(method, argsJson);
        var result = method.Invoke(this, args);
        if (result is Task task) await task;
    }

    // ---- Cached reflection helpers ----

    private static readonly ConcurrentDictionary<(Type, bool), PropertyInfo[]> StatePropsCache = new();
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> ActionCache = new();

    /// <summary>
    /// Returns the cached set of public read/write state properties for a reactive component type,
    /// traversing the inheritance chain up to (but excluding) ReactiveComponent and ComponentBase.
    /// Properties decorated with <c>[Parameter]</c>, <c>[CascadingParameter]</c>,
    /// <c>[Inject]</c>, or <c>[ReactiveIgnore]</c> are excluded.
    /// If <paramref name="requireOptIn"/> is true, only properties with <see cref="ReactiveStateAttribute"/> are included.
    /// </summary>
    internal static PropertyInfo[] StateProperties(Type t, bool requireOptIn = false) =>
        StatePropsCache.GetOrAdd((t, requireOptIn), static key =>
        {
            var (type, optIn) = key;
            var props = new List<PropertyInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var current = type;
            while (current != null && current != typeof(ReactiveComponent) && current != typeof(ComponentBase))
            {
                var declaredProps = current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(p => p.CanRead && p.CanWrite
                        && p.GetCustomAttribute<ParameterAttribute>() is null
                        && p.GetCustomAttribute<CascadingParameterAttribute>() is null
                        && p.GetCustomAttribute<InjectAttribute>() is null
                        && p.GetCustomAttribute<ReactiveIgnoreAttribute>() is null
                        && (!optIn || p.GetCustomAttribute<ReactiveStateAttribute>() is not null));
                foreach (var p in declaredProps)
                {
                    if (names.Add(p.Name))
                    {
                        props.Add(p);
                    }
                }
                current = current.BaseType;
            }
            return props.ToArray();
        });

    private static MethodInfo? ResolveAction(Type type, string action) =>
        ActionCache.GetOrAdd((type, action), static key =>
        {
            var current = key.Item1;
            while (current != null && current != typeof(ReactiveComponent) && current != typeof(ComponentBase))
            {
                var method = current.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m =>
                        m.Name == key.Item2 &&
                        m.GetCustomAttribute<ReactiveActionAttribute>() is not null &&
                        !m.IsSpecialName);
                if (method != null)
                    return method;
                current = current.BaseType;
            }
            return null;
        });

    private static object? ConvertBinding(JsonElement el, Type target)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        if (s is null || s == "null") return null;
        var nt = Nullable.GetUnderlyingType(target) ?? target;
        if (Nullable.GetUnderlyingType(target) != null && string.IsNullOrWhiteSpace(s)) return null;
        if (nt == typeof(string)) return s;
        if (nt == typeof(bool)) return s is "true" or "True" or "on";
        if (nt.IsPrimitive || nt == typeof(decimal)) return Convert.ChangeType(s, nt);
        if (el.ValueKind == JsonValueKind.String)
        {
            if (nt.IsEnum) return Enum.Parse(nt, s, true);
            if (nt == typeof(Guid)) return Guid.Parse(s);
            if (nt == typeof(DateTime)) return DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (nt == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (nt == typeof(DateOnly)) return DateOnly.Parse(s);
            if (nt == typeof(TimeOnly)) return TimeOnly.Parse(s);
        }
        return el.Deserialize(target, Json);
    }

    private static object?[] BindArgs(MethodInfo method, string? argsJson)
    {
        var ps = method.GetParameters();
        if (ps.Length == 0) return [];
        var arr = string.IsNullOrEmpty(argsJson)
            ? Array.Empty<JsonElement>()
            : JsonSerializer.Deserialize<JsonElement[]>(argsJson, Json) ?? [];
        var result = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
            result[i] = i < arr.Length
                ? arr[i].Deserialize(ps[i].ParameterType, Json)
                : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
        return result;
    }
}

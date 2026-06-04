namespace ReactiveBlazor;

/// <summary>
/// Excludes a public read/write property from the serialized reactive state.
/// Use this for properties that hold static or derived data that should not round-trip
/// between client and server on every interaction.
/// </summary>
/// <remarks>
/// <para>
/// By default, every public read/write property declared on a <see cref="ReactiveComponent"/>
/// subclass (excluding <c>[Parameter]</c>, <c>[CascadingParameter]</c>, and <c>[Inject]</c>
/// properties) is treated as reactive state. Applying <c>[ReactiveIgnore]</c> opts the
/// property out of serialization.
/// </para>
/// <example>
/// <code>
/// // This list is rebuilt in OnInitialized — no need to serialize it.
/// [ReactiveIgnore]
/// public string[] Fruits { get; set; } = { "Apple", "Banana", "Cherry" };
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ReactiveIgnoreAttribute : Attribute;

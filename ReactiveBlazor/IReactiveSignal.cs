namespace ReactiveBlazor;

/// <summary>
/// Marker interface for strongly-typed reactive signals.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface on a <c>record</c> (or any reference type) to define a signal that
/// a <see cref="ReactiveComponent"/> action can publish via <see cref="IReactiveSignals"/>, and
/// other components can subscribe to via <c>[OnReactiveSignal&lt;T&gt;]</c> to be re-rendered
/// out-of-band when the signal is published during a dispatch.
/// </para>
/// <example>
/// <code>
/// public sealed record CartChanged : IReactiveSignal;
/// </code>
/// </example>
/// </remarks>
public interface IReactiveSignal;

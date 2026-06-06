namespace ReactiveBlazor;

/// <summary>
/// Per-request bus for publishing reactive signals from within a <see cref="ReactiveComponent"/>
/// action. Components decorated with <c>[OnReactiveSignal&lt;T&gt;]</c> for a published signal
/// are re-rendered out-of-band as part of the same dispatch response.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a scoped service: a fresh instance exists for each HTTP dispatch and is
/// shared between the action being invoked and the dispatch endpoint that reads back the
/// emitted signals.
/// </para>
/// </remarks>
public interface IReactiveSignals
{
    /// <summary>Publishes a signal of type <typeparamref name="T"/> using a default-constructed payload.</summary>
    void Publish<T>() where T : IReactiveSignal, new();

    /// <summary>Publishes the given <paramref name="signal"/>.</summary>
    void Publish<T>(T signal) where T : IReactiveSignal;

    /// <summary>Publishes a signal by runtime <see cref="Type"/> with an optional payload.</summary>
    /// <param name="signalType">A type that implements <see cref="IReactiveSignal"/>.</param>
    /// <param name="payload">Optional payload instance; may be <c>null</c>.</param>
    void Publish(Type signalType, object? payload = null);

    /// <summary>
    /// Returns every signal of type <typeparamref name="T"/> published during this dispatch
    /// in publish order. Returns an empty sequence when none were published. Intended to be
    /// consumed from <c>OnInitialized</c> / <c>OnParametersSet</c> on subscribed components.
    /// </summary>
    IEnumerable<T> GetPublished<T>() where T : IReactiveSignal;

    /// <summary>
    /// Returns <c>true</c> when at least one signal of type <typeparamref name="T"/> was
    /// published during this dispatch.
    /// </summary>
    bool WasPublished<T>() where T : IReactiveSignal;

    /// <summary>
    /// Non-generic form of <see cref="WasPublished{T}"/> for runtime / dynamic scenarios.
    /// </summary>
    bool WasPublished(Type signalType);
}

/// <summary>
/// Default per-request implementation of <see cref="IReactiveSignals"/>.
/// </summary>
internal sealed class ReactiveSignals : IReactiveSignals
{
    private readonly List<(Type Type, object? Payload)> _published = [];
    private readonly HashSet<Type> _publishedTypes = [];

    /// <summary>Distinct signal types that were published during this request.</summary>
    internal IReadOnlyCollection<Type> PublishedTypes => _publishedTypes;

    /// <summary>All published signals in publish order, including payloads.</summary>
    internal IReadOnlyList<(Type Type, object? Payload)> Published => _published;

    public void Publish<T>() where T : IReactiveSignal, new() =>
        Publish(typeof(T), new T());

    public void Publish<T>(T signal) where T : IReactiveSignal
    {
        ArgumentNullException.ThrowIfNull(signal);
        Publish(typeof(T), signal);
    }

    public void Publish(Type signalType, object? payload = null)
    {
        ArgumentNullException.ThrowIfNull(signalType);
        if (!typeof(IReactiveSignal).IsAssignableFrom(signalType))
            throw new ArgumentException(
                $"Type '{signalType.FullName}' does not implement {nameof(IReactiveSignal)}.",
                nameof(signalType));
        _published.Add((signalType, payload));
        _publishedTypes.Add(signalType);
    }

    public IEnumerable<T> GetPublished<T>() where T : IReactiveSignal
    {
        foreach (var (type, payload) in _published)
            if (typeof(T).IsAssignableFrom(type) && payload is T match)
                yield return match;
    }

    public bool WasPublished<T>() where T : IReactiveSignal =>
        WasPublished(typeof(T));

    public bool WasPublished(Type signalType)
    {
        ArgumentNullException.ThrowIfNull(signalType);
        // Fast-path the exact-type case; fall back to assignability for base/interface types.
        if (_publishedTypes.Contains(signalType)) return true;
        foreach (var t in _publishedTypes)
            if (signalType.IsAssignableFrom(t)) return true;
        return false;
    }
}

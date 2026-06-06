namespace ReactiveBlazor;

/// <summary>
/// Subscribes a <see cref="ReactiveComponent"/> class to a reactive signal. When any action
/// publishes a matching signal during a dispatch, every subscribed component on the page is
/// re-rendered out-of-band.
/// </summary>
/// <remarks>
/// Stack multiple attributes to subscribe to several signals.
/// <example>
/// <code>
/// [OnReactiveSignal&lt;CartChanged&gt;]
/// [OnReactiveSignal&lt;CartCleared&gt;]
/// public partial class NavbarCartBadge : ReactiveComponent { }
/// </code>
/// </example>
/// </remarks>
/// <typeparam name="TSignal">The signal type to subscribe to.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class OnReactiveSignalAttribute<TSignal> : OnReactiveSignalAttribute
    where TSignal : IReactiveSignal
{
    /// <summary>Creates a subscription for <typeparamref name="TSignal"/>.</summary>
    public OnReactiveSignalAttribute() : base(typeof(TSignal)) { }
}

/// <summary>
/// Non-generic base / fallback form of <see cref="OnReactiveSignalAttribute{TSignal}"/> for
/// reflection-driven scenarios or when a signal type is only known at runtime.
/// </summary>
/// <remarks>
/// Prefer the generic <c>[OnReactiveSignal&lt;T&gt;]</c> form when the signal type is known
/// at compile time.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class OnReactiveSignalAttribute : Attribute
{
    /// <summary>Creates a subscription for the given signal type.</summary>
    /// <param name="signalType">A type that implements <see cref="IReactiveSignal"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="signalType"/> does not implement <see cref="IReactiveSignal"/>.</exception>
    public OnReactiveSignalAttribute(Type signalType)
    {
        ArgumentNullException.ThrowIfNull(signalType);
        if (!typeof(IReactiveSignal).IsAssignableFrom(signalType))
            throw new ArgumentException(
                $"Type '{signalType.FullName}' does not implement {nameof(IReactiveSignal)}.",
                nameof(signalType));
        SignalType = signalType;
    }

    /// <summary>The subscribed signal type.</summary>
    public Type SignalType { get; }
}

using ReactiveBlazor;

namespace ReactiveBlazor.Demo.Signals;

/// <summary>
/// Published whenever the cart contents change (add, remove, quantity update).
/// Subscribers are re-rendered out-of-band as part of the same dispatch.
/// </summary>
public sealed record CartChanged : IReactiveSignal;

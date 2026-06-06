using ReactiveBlazor;

namespace ReactiveBlazor.Demo.Signals;

/// <summary>Published when a notification is added.</summary>
/// <param name="Id">The id of the new notification.</param>
/// <param name="Level">"info" | "success" | "warning".</param>
public sealed record NotificationAdded(int Id, string Level) : IReactiveSignal;

/// <summary>Published when a notification is marked as read.</summary>
public sealed record NotificationRead(int Id) : IReactiveSignal;

/// <summary>Published when all notifications are cleared.</summary>
public sealed record NotificationsCleared : IReactiveSignal;

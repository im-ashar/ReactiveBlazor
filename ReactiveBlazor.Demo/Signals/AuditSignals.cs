using ReactiveBlazor;

namespace ReactiveBlazor.Demo.Signals;

/// <summary>
/// Published by a privileged action to refresh the admin-only audit log sibling.
/// The subscriber carries <c>[Authorize(Roles = "Admin")]</c>, so a non-admin who somehow
/// receives this signal still never has the sibling rendered or returned.
/// </summary>
public sealed record AuditEntryAdded(string Message) : IReactiveSignal;

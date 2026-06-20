using System.Collections.Concurrent;

namespace ReactiveBlazor.Demo.Services;

/// <summary>
/// Tiny in-memory audit trail for the authorization demo. Privileged actions append entries;
/// an admin-only reactive sibling renders them in response to the <c>AuditEntryAdded</c> signal.
/// Process-wide for simplicity.
/// </summary>
public sealed class AuditLogService
{
    private static readonly ConcurrentQueue<(DateTime At, string Message)> _entries = new();

    public void Add(string message)
    {
        _entries.Enqueue((DateTime.Now, message));
        // Keep only the most recent 20 entries.
        while (_entries.Count > 20 && _entries.TryDequeue(out _)) { }
    }

    public IReadOnlyList<(DateTime At, string Message)> Recent() =>
        _entries.Reverse().ToList();
}

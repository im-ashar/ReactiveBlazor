using System.Collections.Concurrent;
using ReactiveBlazor.Demo.Models;

namespace ReactiveBlazor.Demo.Services;

/// <summary>
/// In-memory notification store for the multi-signal demo. Process-wide for simplicity —
/// not per-user. A real app would scope by session/cookie like <see cref="CartService"/>.
/// </summary>
public sealed class NotificationService
{
    private static int _nextId;
    private static readonly ConcurrentBag<Notification> _items = [];

    public IReadOnlyList<Notification> GetAll() =>
        _items.OrderByDescending(n => n.CreatedAt).ToList();

    public int UnreadCount() => _items.Count(n => !n.IsRead);

    public Notification Add(string level, string message)
    {
        var n = new Notification
        {
            Id = Interlocked.Increment(ref _nextId),
            Level = level,
            Message = message
        };
        _items.Add(n);
        return n;
    }

    /// <summary>Marks the oldest unread notification as read. Returns the marked id, or null if none.</summary>
    public int? MarkOldestUnreadAsRead()
    {
        var oldest = _items
            .Where(n => !n.IsRead)
            .OrderBy(n => n.CreatedAt)
            .FirstOrDefault();
        if (oldest is null) return null;
        oldest.IsRead = true;
        return oldest.Id;
    }

    public void Clear()
    {
        while (_items.TryTake(out _)) { }
    }
}

using System.Collections.Concurrent;

namespace ReactiveBlazor;

/// <summary>
/// Defines a store for tracking one-time use tokens (nonces) to prevent replay attacks.
/// </summary>
public interface IReactiveNonceStore
{
    /// <summary>
    /// Attempts to consume the specified nonce. Returns true if the nonce is valid and has not yet been consumed.
    /// Returns false if it has already been consumed.
    /// </summary>
    /// <param name="nonce">The unique token nonce.</param>
    /// <param name="lifetime">The maximum lifetime the nonce should be remembered.</param>
    /// <returns>True if consumed successfully, false if already consumed.</returns>
    bool TryConsume(string nonce, TimeSpan lifetime);
}

/// <summary>
/// A default, in-memory implementation of <see cref="IReactiveNonceStore"/> using a concurrent dictionary.
/// Suitable for single-instance scenarios.
/// </summary>
public sealed class InMemoryReactiveNonceStore : IReactiveNonceStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryConsume(string nonce, TimeSpan lifetime)
    {
        // Cleanup expired nonces on each write attempt
        CleanupExpired();

        var expiry = DateTimeOffset.UtcNow.Add(lifetime);
        return _consumed.TryAdd(nonce, expiry);
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _consumed)
        {
            if (kvp.Value < now)
            {
                _consumed.TryRemove(kvp.Key, out _);
            }
        }
    }
}

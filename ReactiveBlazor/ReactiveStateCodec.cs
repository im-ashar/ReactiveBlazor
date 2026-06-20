using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReactiveBlazor;

/// <summary>
/// Signs and encrypts the reactive state envelope so the client cannot tamper with it
/// or forge a component type name.
/// </summary>
public interface IReactiveStateCodec
{
    /// <summary>Encrypts and signs the state JSON along with the component type identifier.</summary>
    string Protect(Type componentType, string stateJson);

    /// <summary>
    /// Decrypts and verifies the state token, returning the component type, raw JSON, and the embedded nonce.
    /// If the state shape has changed since the token was issued (e.g. after a deployment),
    /// returns an empty JSON object (<c>{}</c>) so the component starts with default state.
    /// </summary>
    (Type Type, string StateJson, string Nonce) Unprotect(string token);
}

internal sealed class ReactiveStateCodec : IReactiveStateCodec
{
    private readonly IDataProtector _protector;
    private readonly ReactiveComponentRegistry _registry;
    private readonly ILogger<ReactiveStateCodec> _logger;
    private readonly ReactiveOptions _options;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    // Cache the hash per component type and opt-in flag — it never changes at runtime.
    private static readonly ConcurrentDictionary<(Type, bool), uint> StateHashCache = new();

    // The 16-byte user binding tag, computed once per request (the codec is scoped) and reused for
    // every component on the page. Lazily initialized on first use; null sentinel via _userTagComputed.
    private byte[]? _userTag;
    private bool _userTagComputed;

    // Length (bytes) of the user binding tag embedded after the nonce when BindStateToUser is on.
    private const int UserTagLength = 16;

    public ReactiveStateCodec(
        IDataProtectionProvider dp,
        ReactiveComponentRegistry registry,
        ILogger<ReactiveStateCodec> logger,
        IOptions<ReactiveOptions> options,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _options = options.Value;
        // Use a distinct Data Protection purpose when user-binding is enabled so the two token
        // formats can never be cross-decoded, and the off-path stays byte-for-byte unchanged.
        _protector = dp.CreateProtector(
            _options.BindStateToUser ? "ReactiveBlazor.State.v2.userbound" : "ReactiveBlazor.State.v2");
        _registry = registry;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Returns the 16-byte tag binding a token to the current user, or <c>null</c> when user binding
    /// is disabled. Computed once per request (scoped codec) and cached. Anonymous users map to a
    /// fixed "no user" tag, so their tokens are interchangeable only among other anonymous requests.
    /// </summary>
    private byte[]? GetUserTag()
    {
        if (!_options.BindStateToUser)
            return null;
        if (_userTagComputed)
            return _userTag;

        var user = _httpContextAccessor?.HttpContext?.User;
        // Prefer a stable, unique id claim; fall back to the name. Anonymous → empty string.
        var id = user?.Identity?.IsAuthenticated == true
            ? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? user.FindFirst("sub")?.Value
              ?? user.Identity.Name
              ?? ""
            : "";

        // 16 bytes (128 bits) is ample for an equality check and keeps tokens small.
        Span<byte> full = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("ReactiveBlazor.user:" + id), full);
        _userTag = full[..UserTagLength].ToArray();
        _userTagComputed = true;
        return _userTag;
    }

    public string Protect(Type componentType, string stateJson)
    {
        var key = _registry.GetKey(componentType);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var stateBytes = Encoding.UTF8.GetBytes(stateJson);
        var hash = ComputeStateHash(componentType, _options.RequireOptInState);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonceBytes = Guid.NewGuid().ToByteArray();
        var userTag = GetUserTag(); // null unless BindStateToUser is on

        // Binary format: [4-byte key length (BE)][key bytes][4-byte state hash][8-byte timestamp (BE)][16-byte nonce][16-byte user tag, only when bound][state bytes]
        var userTagLen = userTag?.Length ?? 0;
        var payload = new byte[4 + keyBytes.Length + 4 + 8 + 16 + userTagLen + stateBytes.Length];
        var offset = 0;

        // Key length (4 bytes, big-endian)
        payload[offset++] = (byte)(keyBytes.Length >> 24);
        payload[offset++] = (byte)(keyBytes.Length >> 16);
        payload[offset++] = (byte)(keyBytes.Length >> 8);
        payload[offset++] = (byte)keyBytes.Length;

        // Key bytes
        Buffer.BlockCopy(keyBytes, 0, payload, offset, keyBytes.Length);
        offset += keyBytes.Length;

        // State hash (4 bytes, big-endian)
        payload[offset++] = (byte)(hash >> 24);
        payload[offset++] = (byte)(hash >> 16);
        payload[offset++] = (byte)(hash >> 8);
        payload[offset++] = (byte)hash;

        // Timestamp (8 bytes, big-endian)
        payload[offset++] = (byte)(timestamp >> 56);
        payload[offset++] = (byte)(timestamp >> 48);
        payload[offset++] = (byte)(timestamp >> 40);
        payload[offset++] = (byte)(timestamp >> 32);
        payload[offset++] = (byte)(timestamp >> 24);
        payload[offset++] = (byte)(timestamp >> 16);
        payload[offset++] = (byte)(timestamp >> 8);
        payload[offset++] = (byte)timestamp;

        // Nonce bytes (16 bytes)
        Buffer.BlockCopy(nonceBytes, 0, payload, offset, 16);
        offset += 16;

        // User binding tag (16 bytes) — only present when BindStateToUser is on. The distinct
        // protector purpose keeps the two formats from ever being cross-decoded.
        if (userTag is not null)
        {
            Buffer.BlockCopy(userTag, 0, payload, offset, userTagLen);
            offset += userTagLen;
        }

        // State bytes
        Buffer.BlockCopy(stateBytes, 0, payload, offset, stateBytes.Length);

        return Convert.ToBase64String(_protector.Protect(payload));
    }

    public (Type Type, string StateJson, string Nonce) Unprotect(string token)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(token);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The token is not a valid Base64 string.", ex);
        }

        // Throws CryptographicException if the payload was tampered with.
        var payload = _protector.Unprotect(protectedBytes);

        var userTag = GetUserTag(); // null unless BindStateToUser is on
        var userTagLen = userTag?.Length ?? 0;

        // Minimum: 4 (key len) + 1 (key) + 4 (hash) + 8 (timestamp) + 16 (nonce) [+ 16 user tag] = 33 (+16)
        if (payload.Length < 33 + userTagLen)
            throw new InvalidOperationException("Malformed state envelope: too short.");

        // Read key length
        var keyLength = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
        if (keyLength < 0 || 4 + keyLength + 4 + 8 + 16 + userTagLen > payload.Length)
            throw new InvalidOperationException("Malformed state envelope: invalid key length.");

        var key = Encoding.UTF8.GetString(payload, 4, keyLength);
        var type = _registry.GetType(key);

        // Read state hash
        var hashOffset = 4 + keyLength;
        var storedHash = (uint)(
            (payload[hashOffset] << 24) |
            (payload[hashOffset + 1] << 16) |
            (payload[hashOffset + 2] << 8) |
            payload[hashOffset + 3]);

        // Read timestamp
        var tsOffset = hashOffset + 4;
        var storedTimestamp = ((long)payload[tsOffset] << 56) |
                             ((long)payload[tsOffset + 1] << 48) |
                             ((long)payload[tsOffset + 2] << 40) |
                             ((long)payload[tsOffset + 3] << 32) |
                             ((long)payload[tsOffset + 4] << 24) |
                             ((long)payload[tsOffset + 5] << 16) |
                             ((long)payload[tsOffset + 6] << 8) |
                             payload[tsOffset + 7];

        // Read 16-byte Nonce
        var nonceOffset = tsOffset + 8;
        var nonceBytes = new byte[16];
        Buffer.BlockCopy(payload, nonceOffset, nonceBytes, 0, 16);
        var nonce = new Guid(nonceBytes).ToString("N");

        // User binding check (only when BindStateToUser is on). The 16-byte tag follows the nonce.
        // If the token was issued to a different user than the one making this request, reset to
        // default state — same safe, non-throwing behavior as expiry / shape mismatch. Fixed-time
        // compare avoids leaking equality timing.
        var stateOffset = nonceOffset + 16;
        if (userTag is not null)
        {
            var storedTag = new ReadOnlySpan<byte>(payload, stateOffset, userTagLen);
            if (!CryptographicOperations.FixedTimeEquals(storedTag, userTag))
            {
                _logger.LogWarning(
                    "State token for {Component} was issued to a different user (possible cross-user replay). Resetting to default state.",
                    type.Name);
                return (type, "{}", nonce);
            }
            stateOffset += userTagLen;
        }

        // Check token expiration (if lifetime is configured).
        if (_options.StateTokenLifetime > TimeSpan.Zero)
        {
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(storedTimestamp);
            var age = DateTimeOffset.UtcNow - issuedAt;
            if (age > _options.StateTokenLifetime)
            {
                _logger.LogWarning(
                    "State token for {Component} expired (age: {Age}, limit: {Limit}). Resetting to default state.",
                    type.Name, age, _options.StateTokenLifetime);
                return (type, "{}", nonce);
            }
        }

        var currentHash = ComputeStateHash(type, _options.RequireOptInState);

        // If the hash doesn't match, the component's state shape changed since this token
        // was issued (e.g. after a deployment). Return empty state so the component resets
        // cleanly instead of deserializing into a mismatched shape.
        if (storedHash != currentHash)
        {
            _logger.LogInformation(
                "State shape mismatch for {Component} (stored hash {Stored:X8}, current {Current:X8}). Resetting to default state.",
                type.Name, storedHash, currentHash);
            return (type, "{}", nonce);
        }

        var stateJson = Encoding.UTF8.GetString(payload, stateOffset, payload.Length - stateOffset);
        return (type, stateJson, nonce);
    }

    /// <summary>
    /// Computes a stable hash from the component's state property names and types.
    /// Changes when properties are added, removed, renamed, or change type.
    /// </summary>
    private static uint ComputeStateHash(Type componentType, bool requireOptIn) =>
        StateHashCache.GetOrAdd((componentType, requireOptIn), static key =>
        {
            var (t, optIn) = key;
            var props = ReactiveComponent.StateProperties(t, optIn);
            var sb = new StringBuilder();
            foreach (var p in props.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append(p.Name);
                sb.Append(':');
                sb.Append(p.PropertyType.FullName);
                sb.Append(';');
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        });
}

/// <summary>
/// Maps stable string keys to component <see cref="Type"/>s. The key is included in the
/// signed envelope, so an attacker cannot ask the server to instantiate an arbitrary type —
/// only registered <see cref="ReactiveComponent"/> subclasses are allowed.
/// </summary>
public sealed class ReactiveComponentRegistry
{
    private Dictionary<string, Type> _byKey = new(StringComparer.Ordinal);
    private Dictionary<Type, string> _byType = new();
    private Dictionary<Type, HashSet<Type>> _subscribersBySignal = [];
    private volatile bool _frozen;

    private static readonly IReadOnlyCollection<Type> Empty = Array.Empty<Type>();

    /// <summary>Registers a single component type. Throws if the registry is frozen.</summary>
    public void Register(Type t)
    {
        if (_frozen)
            throw new InvalidOperationException("The component registry is frozen and cannot accept new registrations.");
        var key = t.FullName ?? throw new InvalidOperationException($"{t} has no full name.");
        _byKey[key] = t;
        _byType[t] = key;

        // Index any [OnReactiveSignal<T>] / [OnReactiveSignal(typeof(T))] subscriptions.
        foreach (var attr in t.GetCustomAttributes(typeof(OnReactiveSignalAttribute), inherit: true))
        {
            if (attr is OnReactiveSignalAttribute sub)
            {
                if (!_subscribersBySignal.TryGetValue(sub.SignalType, out var set))
                    _subscribersBySignal[sub.SignalType] = set = [];
                set.Add(t);
            }
        }
    }

    /// <summary>Scans an assembly for all concrete <see cref="ReactiveComponent"/> subclasses and registers them.</summary>
    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ReactiveComponent).IsAssignableFrom(t)))
            Register(t);
    }

    /// <summary>
    /// Freezes the registry, making it immutable and thread-safe for concurrent reads.
    /// Called automatically after service registration is complete.
    /// </summary>
    public void Freeze()
    {
        if (_frozen) return;
        // Snapshot into new dictionaries to ensure no ongoing mutation.
        _byKey = new Dictionary<string, Type>(_byKey, StringComparer.Ordinal);
        _byType = new Dictionary<Type, string>(_byType);
        _subscribersBySignal = _subscribersBySignal.ToDictionary(
            kv => kv.Key, kv => new HashSet<Type>(kv.Value));
        _frozen = true;
    }

    /// <summary>Gets the stable key for a registered component type.</summary>
    public string GetKey(Type t) =>
        _byType.TryGetValue(t, out var k) ? k
        : throw new InvalidOperationException($"{t.FullName} is not a registered ReactiveComponent.");

    /// <summary>Gets the component type for a registered key.</summary>
    public Type GetType(string key) =>
        _byKey.TryGetValue(key, out var t) ? t
        : throw new InvalidOperationException($"Unknown component key '{key}'.");

    /// <summary>
    /// Returns the component types that have subscribed to the given signal type via
    /// <c>[OnReactiveSignal&lt;T&gt;]</c>. Returns an empty collection when no subscribers exist.
    /// </summary>
    public IReadOnlyCollection<Type> GetSubscribers(Type signalType) =>
        _subscribersBySignal.TryGetValue(signalType, out var set) ? set : Empty;
}

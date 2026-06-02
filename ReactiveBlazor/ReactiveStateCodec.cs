using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace ReactiveBlazor;

/// <summary>
/// Signs and encrypts the reactive state envelope so the client cannot tamper with it
/// or forge a component type name.
/// </summary>
public interface IReactiveStateCodec
{
    /// <summary>Encrypts and signs the state JSON along with the component type identifier.</summary>
    string Protect(Type componentType, string stateJson);

    /// <summary>Decrypts and verifies the state token, returning the component type and raw JSON.</summary>
    (Type Type, string StateJson) Unprotect(string token);
}

internal sealed class ReactiveStateCodec : IReactiveStateCodec
{
    private readonly IDataProtector _protector;
    private readonly ReactiveComponentRegistry _registry;

    public ReactiveStateCodec(IDataProtectionProvider dp, ReactiveComponentRegistry registry)
    {
        _protector = dp.CreateProtector("ReactiveBlazor.State.v1");
        _registry = registry;
    }

    public string Protect(Type componentType, string stateJson)
    {
        var key = _registry.GetKey(componentType);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var stateBytes = Encoding.UTF8.GetBytes(stateJson);

        // Length-prefixed binary format: [4-byte key length (big-endian)][key bytes][state bytes]
        var payload = new byte[4 + keyBytes.Length + stateBytes.Length];
        payload[0] = (byte)(keyBytes.Length >> 24);
        payload[1] = (byte)(keyBytes.Length >> 16);
        payload[2] = (byte)(keyBytes.Length >> 8);
        payload[3] = (byte)keyBytes.Length;
        Buffer.BlockCopy(keyBytes, 0, payload, 4, keyBytes.Length);
        Buffer.BlockCopy(stateBytes, 0, payload, 4 + keyBytes.Length, stateBytes.Length);

        return _protector.Protect(Convert.ToBase64String(payload));
    }

    public (Type Type, string StateJson) Unprotect(string token)
    {
        // Throws CryptographicException if the payload was tampered with.
        var raw = _protector.Unprotect(token);
        var payload = Convert.FromBase64String(raw);

        if (payload.Length < 4)
            throw new InvalidOperationException("Malformed state envelope: too short.");

        var keyLength = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
        if (keyLength < 0 || 4 + keyLength > payload.Length)
            throw new InvalidOperationException("Malformed state envelope: invalid key length.");

        var key = Encoding.UTF8.GetString(payload, 4, keyLength);
        var stateJson = Encoding.UTF8.GetString(payload, 4 + keyLength, payload.Length - 4 - keyLength);

        return (_registry.GetType(key), stateJson);
    }
}

/// <summary>
/// Maps stable string keys to component <see cref="Type"/>s. The key is included in the
/// signed envelope, so an attacker cannot ask the server to instantiate an arbitrary type —
/// only registered <see cref="ReactiveComponent"/> subclasses are allowed.
/// </summary>
public sealed class ReactiveComponentRegistry
{
    private readonly Dictionary<string, Type> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = new();

    /// <summary>Registers a single component type.</summary>
    public void Register(Type t)
    {
        var key = t.FullName ?? throw new InvalidOperationException($"{t} has no full name.");
        _byKey[key] = t;
        _byType[t] = key;
    }

    /// <summary>Scans an assembly for all concrete <see cref="ReactiveComponent"/> subclasses and registers them.</summary>
    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ReactiveComponent).IsAssignableFrom(t)))
            Register(t);
    }

    /// <summary>Gets the stable key for a registered component type.</summary>
    public string GetKey(Type t) =>
        _byType.TryGetValue(t, out var k) ? k
        : throw new InvalidOperationException($"{t.FullName} is not a registered ReactiveComponent.");

    /// <summary>Gets the component type for a registered key.</summary>
    public Type GetType(string key) =>
        _byKey.TryGetValue(key, out var t) ? t
        : throw new InvalidOperationException($"Unknown component key '{key}'.");
}

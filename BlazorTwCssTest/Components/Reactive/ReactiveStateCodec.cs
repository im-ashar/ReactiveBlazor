using System.Reflection;
using Microsoft.AspNetCore.DataProtection;

namespace ReactiveBlazor;

/// <summary>Signs/encrypts the state envelope so the client cannot tamper with it or forge a component type.</summary>
public interface IReactiveStateCodec
{
    string Protect(Type componentType, string stateJson);
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
        return _protector.Protect(key + "|" + stateJson);
    }

    public (Type Type, string StateJson) Unprotect(string token)
    {
        // Throws CryptographicException if the payload was tampered with - let it bubble to a 400.
        var payload = _protector.Unprotect(token);
        var idx = payload.IndexOf('|');
        if (idx < 0) throw new InvalidOperationException("Malformed state envelope.");
        return (_registry.GetType(payload[..idx]), payload[(idx + 1)..]);
    }
}

/// <summary>
/// Maps a stable string key to a component Type. The key is included in the *signed* envelope, so an
/// attacker cannot ask the server to instantiate an arbitrary type - only registered ReactiveComponents.
/// </summary>
public sealed class ReactiveComponentRegistry
{
    private readonly Dictionary<string, Type> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = new();

    public void Register(Type t)
    {
        var key = t.FullName ?? throw new InvalidOperationException($"{t} has no full name.");
        _byKey[key] = t;
        _byType[t] = key;
    }

    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ReactiveComponent).IsAssignableFrom(t)))
            Register(t);
    }

    public string GetKey(Type t) =>
        _byType.TryGetValue(t, out var k) ? k
        : throw new InvalidOperationException($"{t.FullName} is not a registered ReactiveComponent.");

    public Type GetType(string key) =>
        _byKey.TryGetValue(key, out var t) ? t
        : throw new InvalidOperationException($"Unknown component key '{key}'.");
}

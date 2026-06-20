using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace ReactiveBlazor;

/// <summary>
/// The outcome of authorizing a reactive action, mapped to an HTTP status by the dispatch endpoint.
/// </summary>
internal enum ReactiveAuthResult
{
    /// <summary>The caller is authorized; proceed with the action.</summary>
    Allowed,

    /// <summary>The caller is authenticated but lacks the required policy/role — maps to <c>403</c>.</summary>
    Forbidden,

    /// <summary>The caller is not authenticated — maps to <c>401</c> so the client can re-challenge.</summary>
    Unauthenticated,
}

/// <summary>
/// Cached authorization metadata for a component type or action method: the
/// <see cref="IAuthorizeData"/> declared via <c>[Authorize]</c>, whether <c>[AllowAnonymous]</c>
/// is present (which bypasses authorization), and whether any <c>[Authorize]</c> exists at all.
/// </summary>
internal sealed record ReactiveAuthMetadata(
    IReadOnlyList<IAuthorizeData> AuthorizeData,
    bool HasAllowAnonymous,
    bool HasAuthorize)
{
    /// <summary>Shared instance for members that declare no authorization at all.</summary>
    internal static readonly ReactiveAuthMetadata None = new(Array.Empty<IAuthorizeData>(), false, false);

    /// <summary>
    /// True when authorization must be evaluated: there is at least one <c>[Authorize]</c>
    /// and no <c>[AllowAnonymous]</c> override.
    /// </summary>
    internal bool RequiresAuthorization => HasAuthorize && !HasAllowAnonymous;
}

/// <summary>
/// Reads and combines the framework's standard <c>[Authorize]</c> / <c>[AllowAnonymous]</c>
/// attributes exactly as ASP.NET Core does (via <c>AuthorizationPolicy.CombineAsync</c>),
/// so roles, named policies, authentication schemes, and custom requirements all behave
/// identically to MVC / SignalR. The library never defines its own authorization attributes.
/// </summary>
internal static class ReactiveAuthorization
{
    // Attribute metadata never changes at runtime, so cache it per member like the
    // existing ActionCache / StateHashCache patterns in ReactiveComponent / ReactiveStateCodec.
    private static readonly ConcurrentDictionary<Type, ReactiveAuthMetadata> TypeMetadataCache = new();
    private static readonly ConcurrentDictionary<MethodInfo, ReactiveAuthMetadata> MethodMetadataCache = new();

    /// <summary>
    /// Gets the (cached) authorization metadata declared on a component type, walking its
    /// inheritance chain via <c>inherit: true</c> so base-class <c>[Authorize]</c> applies.
    /// </summary>
    internal static ReactiveAuthMetadata GetMetadata(Type componentType) =>
        TypeMetadataCache.GetOrAdd(componentType, static t => BuildMetadata(
            t.GetCustomAttributes(inherit: true)));

    /// <summary>
    /// Gets the (cached) authorization metadata for an action method, combining the attributes
    /// declared on the method itself with those declared on its component type. <c>[AllowAnonymous]</c>
    /// at either level bypasses authorization, matching ASP.NET's "nearest AllowAnonymous wins" rule.
    /// </summary>
    internal static ReactiveAuthMetadata GetMetadata(Type componentType, MethodInfo method) =>
        MethodMetadataCache.GetOrAdd(method, m =>
        {
            var typeMeta = GetMetadata(componentType);
            var methodMeta = BuildMetadata(m.GetCustomAttributes(inherit: true));

            // Merge: AuthorizeData accumulates (AND semantics via CombineAsync);
            // AllowAnonymous / Authorize are OR'd across the two scopes.
            var combined = new List<IAuthorizeData>(typeMeta.AuthorizeData);
            combined.AddRange(methodMeta.AuthorizeData);
            return new ReactiveAuthMetadata(
                combined,
                typeMeta.HasAllowAnonymous || methodMeta.HasAllowAnonymous,
                typeMeta.HasAuthorize || methodMeta.HasAuthorize);
        });

    private static ReactiveAuthMetadata BuildMetadata(object[] attributes)
    {
        List<IAuthorizeData>? authorizeData = null;
        var hasAllowAnonymous = false;

        foreach (var attr in attributes)
        {
            if (attr is IAllowAnonymous)
                hasAllowAnonymous = true;
            if (attr is IAuthorizeData data)
                (authorizeData ??= []).Add(data);
        }

        if (authorizeData is null && !hasAllowAnonymous)
            return ReactiveAuthMetadata.None;

        return new ReactiveAuthMetadata(
            (IReadOnlyList<IAuthorizeData>?)authorizeData ?? Array.Empty<IAuthorizeData>(),
            hasAllowAnonymous,
            authorizeData is { Count: > 0 });
    }

    /// <summary>
    /// Resolves the combined <see cref="AuthorizationPolicy"/> for the given metadata using the
    /// app's <see cref="IAuthorizationPolicyProvider"/>. Returns <c>null</c> when no authorization
    /// is required (no <c>[Authorize]</c>, or an <c>[AllowAnonymous]</c> override).
    /// </summary>
    internal static async Task<AuthorizationPolicy?> ResolvePolicyAsync(
        IAuthorizationPolicyProvider provider,
        ReactiveAuthMetadata meta)
    {
        if (!meta.RequiresAuthorization)
            return null;

        // The framework's own combiner: resolves named policies, merges roles/schemes,
        // and AND-combines multiple [Authorize] attributes.
        return await AuthorizationPolicy.CombineAsync(provider, meta.AuthorizeData);
    }
}

/// <summary>
/// Per-dispatch authorization gate. Holds the live <see cref="ClaimsPrincipal"/> for the request
/// and evaluates component-level and action-level <c>[Authorize]</c> against the app's
/// <see cref="IAuthorizationService"/>. Decisions are computed asynchronously (outside the
/// synchronous render tree) and cached per component type for the duration of the dispatch.
/// </summary>
internal interface IReactiveAuthorizationContext
{
    /// <summary>
    /// Pre-computes and caches the component-level authorization decision for each distinct
    /// component type involved in this dispatch, so later synchronous reads are free.
    /// </summary>
    Task PrecomputeAsync(IEnumerable<Type> componentTypes);

    /// <summary>
    /// Returns the cached component-level decision. Defaults to <c>false</c> (deny) when no
    /// decision was pre-computed, so callers fail closed.
    /// </summary>
    bool IsComponentAuthorizedCached(Type componentType);

    /// <summary>
    /// Evaluates (and caches) the component-level authorization decision. Used from
    /// <see cref="ReactiveComponent.SetParametersAsync"/>, which also covers the initial SSR
    /// render path where no pre-computation ran.
    /// </summary>
    Task<bool> IsComponentAuthorizedAsync(Type componentType);

    /// <summary>
    /// Authorizes a reactive action method (combining method- and type-level attributes).
    /// </summary>
    Task<ReactiveAuthResult> AuthorizeActionAsync(Type componentType, MethodInfo method, object? resource);
}

/// <summary>
/// Default <see cref="IReactiveAuthorizationContext"/>. Constructed per dispatch by the endpoint
/// (and resolvable by rendered components via the service-provider shim). Fails <b>closed</b>:
/// any error during policy resolution or evaluation denies access rather than leaking a 500.
/// </summary>
internal sealed class ReactiveAuthorizationContext : IReactiveAuthorizationContext
{
    private readonly ClaimsPrincipal _user;
    private readonly IAuthorizationService? _authorizationService;
    private readonly IAuthorizationPolicyProvider? _policyProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Type, bool> _componentDecisions = new();

    public ReactiveAuthorizationContext(
        ClaimsPrincipal user,
        IAuthorizationService? authorizationService,
        IAuthorizationPolicyProvider? policyProvider,
        ILogger logger)
    {
        _user = user;
        _authorizationService = authorizationService;
        _policyProvider = policyProvider;
        _logger = logger;
    }

    private bool IsAuthenticated => _user.Identity?.IsAuthenticated == true;

    public async Task PrecomputeAsync(IEnumerable<Type> componentTypes)
    {
        foreach (var type in componentTypes)
            await IsComponentAuthorizedAsync(type);
    }

    public bool IsComponentAuthorizedCached(Type componentType) =>
        _componentDecisions.TryGetValue(componentType, out var allowed) && allowed;

    public async Task<bool> IsComponentAuthorizedAsync(Type componentType)
    {
        if (_componentDecisions.TryGetValue(componentType, out var cached))
            return cached;

        var meta = ReactiveAuthorization.GetMetadata(componentType);
        var allowed = (await EvaluateAsync(meta, componentType.Name, resource: null)) == ReactiveAuthResult.Allowed;
        _componentDecisions[componentType] = allowed;
        return allowed;
    }

    public Task<ReactiveAuthResult> AuthorizeActionAsync(Type componentType, MethodInfo method, object? resource)
    {
        var meta = ReactiveAuthorization.GetMetadata(componentType, method);
        return EvaluateAsync(meta, $"{componentType.Name}.{method.Name}", resource);
    }

    private async Task<ReactiveAuthResult> EvaluateAsync(ReactiveAuthMetadata meta, string target, object? resource)
    {
        if (!meta.RequiresAuthorization)
            return ReactiveAuthResult.Allowed;

        // [Authorize] is declared but the app registered no authorization services: misconfiguration.
        // Fail closed rather than silently allowing protected content through.
        if (_authorizationService is null || _policyProvider is null)
        {
            _logger.LogError(
                "'{Target}' declares [Authorize] but no authorization services are registered. " +
                "Call builder.Services.AddAuthorization(). Denying access.", target);
            return Deny();
        }

        try
        {
            var policy = await ReactiveAuthorization.ResolvePolicyAsync(_policyProvider, meta);
            if (policy is null)
                return ReactiveAuthResult.Allowed;

            var result = await _authorizationService.AuthorizeAsync(_user, resource, policy);
            if (result.Succeeded)
                return ReactiveAuthResult.Allowed;

            _logger.LogWarning("Authorization denied for '{Target}'.", target);
            return Deny();
        }
        catch (Exception ex)
        {
            // Policy not found, handler threw, etc. Fail closed — never surface as a 500.
            _logger.LogError(ex, "Authorization evaluation failed for '{Target}'. Denying access.", target);
            return Deny();
        }
    }

    private ReactiveAuthResult Deny() =>
        IsAuthenticated ? ReactiveAuthResult.Forbidden : ReactiveAuthResult.Unauthenticated;
}

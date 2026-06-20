using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReactiveBlazor;

/// <summary>
/// Extension methods for registering ReactiveBlazor services.
/// </summary>
public static class ReactiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers ReactiveBlazor services. Pass the assemblies that contain your
    /// <see cref="ReactiveComponent"/> subclasses.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback for <see cref="ReactiveOptions"/>.</param>
    /// <param name="assemblies">
    /// Assemblies to scan for reactive components. Defaults to the calling assembly if none are provided.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Data Protection</b>: ReactiveBlazor requires ASP.NET Data Protection to be registered.
    /// Call <c>builder.Services.AddDataProtection()</c> yourself if you need custom key storage
    /// or application name settings. If Data Protection is not registered, ASP.NET Core's
    /// defaults will be used automatically.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddReactiveComponents(
        this IServiceCollection services,
        Action<ReactiveOptions>? configure = null,
        params Assembly[] assemblies)
    {
        services.AddHttpContextAccessor();
        services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<ReactiveOptions>(_ => { });

        var registry = new ReactiveComponentRegistry();
        foreach (var asm in assemblies.DefaultIfEmpty(Assembly.GetCallingAssembly()))
            registry.RegisterAssembly(asm);
        registry.Freeze();

        services.AddSingleton(registry);
        services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
        services.AddScoped<IReactiveSignals, ReactiveSignals>();
        services.TryAddSingleton<IReactiveNonceStore, InMemoryReactiveNonceStore>();
        return services;
    }
}

/// <summary>
/// Extension methods for mapping the ReactiveBlazor dispatch endpoint.
/// </summary>
public static class ReactiveEndpointRouteBuilderExtensions
{
    private sealed record ComponentDto(string Id, string State);
    private sealed record DispatchRequest(
        string TargetId, string? Action, object?[]? Args, Dictionary<string, object?>? Bindings, List<ComponentDto> Components);

    /// <summary>
    /// Wraps an existing service provider to substitute <see cref="NavigationManager"/>
    /// with a properly initialized instance for reactive dispatch rendering.
    /// Components like <c>NavLink</c> require <c>NavigationManager</c> to be initialized,
    /// which does not happen when using <see cref="HtmlRenderer"/> directly.
    /// </summary>
    private sealed class ReactiveServiceProvider(IServiceProvider inner, NavigationManager navigationManager)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(NavigationManager) ? navigationManager : inner.GetService(serviceType);
    }

    /// <summary>
    /// Maps the endpoint the ReactiveBlazor JS runtime POSTs to.
    /// Call after <c>MapRazorComponents()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapReactiveComponents(this IEndpointRouteBuilder endpoints)
    {
        var dispatchPath = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ReactiveOptions>>().Value.DispatchPath;

        endpoints.MapPost(dispatchPath, async (
            HttpContext http,
            IReactiveStateCodec codec,
            IAntiforgery antiforgery,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            IOptions<ReactiveOptions> options) =>
        {
            var logger = loggerFactory.CreateLogger("ReactiveBlazor.Dispatch");
            var opts = options.Value;
            var ct = http.RequestAborted;

            // --- Antiforgery validation ---
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException ex)
            {
                logger.LogWarning(ex, "Antiforgery token validation failed.");
                return Results.BadRequest("Antiforgery validation failed. Please refresh the page.");
            }

            // --- Parse request body ---
            var req = await http.Request.ReadFromJsonAsync<DispatchRequest>(ct)
                      ?? throw new BadHttpRequestException("Empty dispatch body.");

            if (string.IsNullOrEmpty(req.TargetId))
            {
                logger.LogWarning("TargetId is missing or empty.");
                return Results.BadRequest("Target component ID is missing.");
            }

            if (req.Components == null || req.Components.Count == 0)
            {
                logger.LogWarning("No components provided in the dispatch request.");
                return Results.BadRequest("Component list is empty.");
            }

            if (req.Components.Count > opts.MaxComponentsPerDispatch)
            {
                logger.LogWarning("Dispatch request contains {Count} components, exceeding limit {Limit}.",
                    req.Components.Count, opts.MaxComponentsPerDispatch);
                return Results.BadRequest("Too many components in dispatch request.");
            }

            // --- Validate and decrypt all components ---
            var decryptedComponents = new List<(string Id, Type Type, string StateJson, string Nonce)>();
            var targetComponentFound = false;

            foreach (var comp in req.Components)
            {
                if (string.IsNullOrEmpty(comp.State))
                {
                    logger.LogWarning("State token for component {ComponentId} is missing.", comp.Id);
                    return Results.BadRequest("State token is missing.");
                }

                if (comp.State.Length > opts.MaxTokenBytes)
                {
                    logger.LogWarning("Encrypted state token size {Size} bytes for component {ComponentId} exceeds limit {Limit}.",
                        comp.State.Length, comp.Id, opts.MaxTokenBytes);
                    return Results.BadRequest("State token exceeds maximum allowed size.");
                }

                Type type;
                string stateJson;
                string nonce;
                try
                {
                    (type, stateJson, nonce) = codec.Unprotect(comp.State);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                                or InvalidOperationException)
                {
                    logger.LogWarning(ex, "State decryption/validation failed for component {ComponentId}.", comp.Id);
                    return Results.BadRequest("Invalid or expired state. Please refresh the page.");
                }

                if (stateJson.Length > opts.MaxStateBytes)
                {
                    logger.LogWarning("State size {Size} bytes for component {ComponentId} exceeds limit {Limit}.",
                        stateJson.Length, comp.Id, opts.MaxStateBytes);
                    return Results.BadRequest("State exceeds maximum allowed size.");
                }

                decryptedComponents.Add((comp.Id, type, stateJson, nonce));

                if (comp.Id == req.TargetId)
                {
                    targetComponentFound = true;
                }
            }

            if (!targetComponentFound)
            {
                logger.LogWarning("Target component {TargetId} not found in the list of components.", req.TargetId);
                return Results.BadRequest("Target component not found.");
            }

            // --- Replay check for target action ---
            if (!string.IsNullOrEmpty(req.Action))
            {
                var targetCompData = decryptedComponents.First(c => c.Id == req.TargetId);
                var method = targetCompData.Type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == req.Action && m.GetCustomAttribute<ReactiveActionAttribute>() is not null);

                if (method != null)
                {
                    var attr = method.GetCustomAttribute<ReactiveActionAttribute>();
                    if (attr != null && attr.RequireOneTimeToken)
                    {
                        var nonceStore = http.RequestServices.GetRequiredService<IReactiveNonceStore>();
                        var tokenLifetime = opts.StateTokenLifetime > TimeSpan.Zero ? opts.StateTokenLifetime : TimeSpan.FromHours(24);
                        if (!nonceStore.TryConsume(targetCompData.Nonce, tokenLifetime))
                        {
                            logger.LogWarning("Action '{Action}' requires a one-time token, but nonce '{Nonce}' was already consumed (possible replay attack).", req.Action, targetCompData.Nonce);
                            return Results.BadRequest("This request has already been processed.");
                        }
                    }
                }
            }

            // --- Render the components ---
            try
            {
                ct.ThrowIfCancellationRequested();
                var reactiveServices = new ReactiveServiceProvider(services, new ReactiveNavigationManager(http));
                await using var renderer = new HtmlRenderer(reactiveServices, loggerFactory);
                var registry = services.GetRequiredService<ReactiveComponentRegistry>();
                var signals = (ReactiveSignals)services.GetRequiredService<IReactiveSignals>();

                var updates = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var results = new Dictionary<string, string>();

                    // 1. Render the target component first so its action runs and may publish signals.
                    var targetComponent = decryptedComponents.First(c => c.Id == req.TargetId);
                    logger.LogDebug("Dispatching action '{Action}' on target component {Type} (id: {Id}, state: {StateSize} bytes).",
                        req.Action ?? "(bind-only)", targetComponent.Type.Name, targetComponent.Id, targetComponent.StateJson.Length);

                    var targetParams = ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["ReactiveState"] = targetComponent.StateJson,
                        ["ReactiveAction"] = req.Action,
                        ["ReactiveArgs"] = req.Args is null ? null : JsonSerializer.Serialize(req.Args),
                        ["ReactiveBindings"] = req.Bindings is null ? null : JsonSerializer.Serialize(req.Bindings),
                    });

                    var targetOutput = await renderer.RenderComponentAsync(targetComponent.Type, targetParams);
                    var targetHtml = targetOutput.ToHtmlString();
                    ValidateBoundary(targetHtml, targetComponent.Type);
                    results[targetComponent.Id] = targetHtml;

                    // 2. Compute the set of component types subscribed to any signal published by the action.
                    var emitted = signals.PublishedTypes;
                    var refreshTypes = new HashSet<Type>();
                    foreach (var sig in emitted)
                    {
                        foreach (var sub in registry.GetSubscribers(sig))
                            refreshTypes.Add(sub);
                    }

                    // 3. Render only siblings whose type is subscribed to an emitted signal.
                    var refreshed = 0;
                    foreach (var (id, type, stateJson, _) in decryptedComponents)
                    {
                        if (id == req.TargetId) continue;
                        if (!refreshTypes.Contains(type)) continue;

                        var siblingParams = ParameterView.FromDictionary(new Dictionary<string, object?>
                        {
                            ["ReactiveState"] = stateJson
                        });

                        var siblingOutput = await renderer.RenderComponentAsync(type, siblingParams);
                        var siblingHtml = siblingOutput.ToHtmlString();
                        ValidateBoundary(siblingHtml, type);
                        results[id] = siblingHtml;
                        refreshed++;
                    }

                    logger.LogDebug("Signals: {EmittedCount} emitted, refreshing {RefreshedCount} subscribed siblings.",
                        emitted.Count, refreshed);

                    return results;
                });

                return Results.Json(updates);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Authorization failures thrown from inside an action map to 403, not a leaked 500.
                logger.LogWarning(ex, "Action '{Action}' denied (unauthorized).", req.Action);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (ReactiveBindingException ex)
            {
                // Malformed client-supplied binding/argument input is a bad request, not a server error.
                logger.LogWarning(ex, "Invalid binding or argument input for action '{Action}'.", req.Action);
                return Results.BadRequest("Invalid input. Please refresh the page and try again.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                logger.LogWarning(ex, "Action dispatch failed.");
                return Results.BadRequest("Action dispatch failed. The request may be invalid.");
            }
            catch (Exception ex)
            {
                // Unexpected exception from user action code: log server-side, return a sanitized
                // 500 with no body so stack traces / internals are never leaked to the client.
                logger.LogError(ex, "Unexpected error during reactive dispatch for action '{Action}'.", req.Action);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        return endpoints;
    }

    /// <summary>
    /// Guards against the most common ReactiveBlazor authoring mistake: a component that renders
    /// markup <em>outside</em> its <see cref="ReactiveRoot"/> (or omits it entirely). The client
    /// morphs the returned HTML onto the single <c>data-component</c> boundary element, so the
    /// rendered output's first element must be that boundary. If a component renders a wrapper
    /// (e.g. a page header) around <c>&lt;ReactiveRoot&gt;</c>, the boundary ends up nested and
    /// the morph duplicates the whole subtree into the previous one. We fail fast with an
    /// actionable message instead of producing corrupted DOM.
    /// </summary>
    private static void ValidateBoundary(string html, Type componentType)
    {
        // The boundary div emitted by ReactiveRoot carries data-component as its first attribute.
        // A correctly-authored component renders it as the outermost element, so the first '<'
        // tag in the trimmed output must contain data-component before any other element opens.
        var trimmed = html.AsSpan().TrimStart();
        var firstTag = trimmed.IndexOf('<');
        if (firstTag != 0)
        {
            // Leading text/whitespace before the first element — boundary can't be the root.
            throw BoundaryError(componentType);
        }

        // Find the end of the first opening tag and ensure it carries data-component.
        var tagEnd = trimmed.IndexOf('>');
        if (tagEnd < 0 || !trimmed[..tagEnd].Contains("data-component", StringComparison.Ordinal))
            throw BoundaryError(componentType);
    }

    private static InvalidOperationException BoundaryError(Type componentType) =>
        new($"Reactive component '{componentType.Name}' must render <ReactiveRoot Owner=\"this\"> " +
            "as its single outermost element, with no other markup outside it. " +
            "Move any surrounding markup (page headers, wrappers, sibling components) into a parent " +
            "page, or inside the ReactiveRoot. Otherwise the client morph nests the component into itself.");
}

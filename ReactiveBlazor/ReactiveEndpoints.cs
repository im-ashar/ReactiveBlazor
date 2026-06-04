using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }
}

/// <summary>
/// Extension methods for mapping the ReactiveBlazor dispatch endpoint.
/// </summary>
public static class ReactiveEndpointRouteBuilderExtensions
{
    private sealed record DispatchRequest(
        string State, string? Action, object?[]? Args, Dictionary<string, object?>? Bindings);

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

            // --- Pre-decryption size check ---
            if (req.State.Length > opts.MaxTokenBytes)
            {
                logger.LogWarning("Encrypted state token size {Size} bytes exceeds limit {Limit}.",
                    req.State.Length, opts.MaxTokenBytes);
                return Results.BadRequest("State token exceeds maximum allowed size.");
            }

            // --- Unprotect and validate state ---
            (Type type, string stateJson) stateResult;
            try
            {
                stateResult = codec.Unprotect(req.State);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                            or InvalidOperationException)
            {
                logger.LogWarning(ex, "State decryption/validation failed.");
                return Results.BadRequest("Invalid or expired state. Please refresh the page.");
            }

            var (type, stateJson) = stateResult;

            if (stateJson.Length > opts.MaxStateBytes)
            {
                logger.LogWarning("State size {Size} bytes exceeds limit {Limit}.",
                    stateJson.Length, opts.MaxStateBytes);
                return Results.BadRequest("State exceeds maximum allowed size.");
            }

            logger.LogDebug("Dispatching action '{Action}' on component (state: {StateSize} bytes).",
                req.Action ?? "(bind-only)", stateJson.Length);

            // --- Render the component with restored state + action ---
            try
            {
                ct.ThrowIfCancellationRequested();
                await using var renderer = new HtmlRenderer(services, loggerFactory);
                var html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["ReactiveState"] = stateJson,
                        ["ReactiveAction"] = req.Action,
                        ["ReactiveArgs"] = req.Args is null ? null : JsonSerializer.Serialize(req.Args),
                        ["ReactiveBindings"] = req.Bindings is null ? null : JsonSerializer.Serialize(req.Bindings),
                    });
                    var output = await renderer.RenderComponentAsync(type, parameters);
                    return output.ToHtmlString();
                });

                return Results.Content(html, "text/html");
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                logger.LogWarning(ex, "Action dispatch failed for component {Component}.", type.Name);
                return Results.BadRequest("Action dispatch failed. The request may be invalid.");
            }
        });

        return endpoints;
    }
}

using System.Diagnostics;
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
    public static IServiceCollection AddReactiveComponents(
        this IServiceCollection services,
        Action<ReactiveOptions>? configure = null,
        params Assembly[] assemblies)
    {
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<ReactiveOptions>(_ => { });

        var registry = new ReactiveComponentRegistry();
        foreach (var asm in assemblies.DefaultIfEmpty(Assembly.GetCallingAssembly()))
            registry.RegisterAssembly(asm);

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
        endpoints.MapPost("/_reactive/dispatch", async (
            HttpContext http,
            IReactiveStateCodec codec,
            IAntiforgery antiforgery,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            IOptions<ReactiveOptions> options) =>
        {
            var logger = loggerFactory.CreateLogger("ReactiveBlazor.Dispatch");
            var opts = options.Value;

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
            var req = await http.Request.ReadFromJsonAsync<DispatchRequest>()
                      ?? throw new BadHttpRequestException("Empty dispatch body.");

            // --- Unprotect and validate state ---
            var sw = Stopwatch.StartNew();
            var (type, stateJson) = codec.Unprotect(req.State);

            if (stateJson.Length > opts.MaxStateBytes)
            {
                logger.LogWarning("State size {Size} bytes exceeds limit {Limit} for {Component}.",
                    stateJson.Length, opts.MaxStateBytes, type.Name);
                return Results.BadRequest($"State exceeds maximum size of {opts.MaxStateBytes} bytes.");
            }

            logger.LogDebug("Dispatching action '{Action}' on {Component} (state: {StateSize} bytes).",
                req.Action ?? "(bind-only)", type.Name, stateJson.Length);

            // --- Render the component with restored state + action ---
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

            sw.Stop();
            logger.LogDebug("Rendered {Component} in {Elapsed}ms.", type.Name, sw.ElapsedMilliseconds);

            return Results.Content(html, "text/html");
        });

        return endpoints;
    }
}

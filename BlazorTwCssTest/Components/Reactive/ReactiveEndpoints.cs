using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReactiveBlazor;

public static class ReactiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers ReactiveBlazor. Pass the assemblies that contain your ReactiveComponents
    /// (defaults to the calling assembly).
    /// </summary>
    public static IServiceCollection AddReactiveComponents(
        this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

        var registry = new ReactiveComponentRegistry();
        foreach (var asm in assemblies.DefaultIfEmpty(Assembly.GetCallingAssembly()))
            registry.RegisterAssembly(asm);

        services.AddSingleton(registry);
        services.AddScoped<IReactiveStateCodec, ReactiveStateCodec>();
        return services;
    }
}

public static class ReactiveEndpoints
{
    private sealed record DispatchRequest(
        string State, string? Action, object?[]? Args, Dictionary<string, object?>? Bindings);

    /// <summary>Maps the endpoint the JS runtime POSTs to. Call after MapRazorComponents().</summary>
    public static IEndpointRouteBuilder MapReactiveComponents(
        this IEndpointRouteBuilder endpoints, string pattern = "/_reactive/dispatch")
    {
        endpoints.MapPost(pattern, async (
            HttpContext http,
            IReactiveStateCodec codec,
            IAntiforgery antiforgery,
            IServiceProvider services,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException ex)
            {
                var logger = loggerFactory.CreateLogger("ReactiveBlazor.ReactiveEndpoints");
                logger.LogWarning(ex, "Antiforgery token validation failed.");
                return Results.BadRequest("Antiforgery validation failed. Please refresh the page.");
            }

            var req = await http.Request.ReadFromJsonAsync<DispatchRequest>()
                      ?? throw new BadHttpRequestException("Empty dispatch body.");

            var (type, stateJson) = codec.Unprotect(req.State);

            // Render the component out-of-band, feeding it the prior state + the action to run.
            // ReactiveRoot inside the component re-emits the (now updated) signed state.
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
        });

        return endpoints;
    }
}

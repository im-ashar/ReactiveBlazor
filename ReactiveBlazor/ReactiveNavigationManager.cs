using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace ReactiveBlazor;

/// <summary>
/// A minimal <see cref="NavigationManager"/> used during reactive dispatch rendering.
/// Provides the current request URL to components that depend on <c>NavigationManager</c>
/// (such as <c>NavLink</c>) without requiring the full Blazor SSR rendering pipeline.
/// </summary>
internal sealed class ReactiveNavigationManager : NavigationManager
{
    public ReactiveNavigationManager(HttpContext context)
    {
        var request = context.Request;
        var baseUri = $"{request.Scheme}://{request.Host}{request.PathBase}/";
        var uri = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        Initialize(baseUri, uri);
    }

    /// <inheritdoc />
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        throw new InvalidOperationException(
            "Client-side navigation is not available during reactive dispatch. " +
            "Set the RedirectUrl property on your ReactiveComponent to redirect after an action.");
    }
}

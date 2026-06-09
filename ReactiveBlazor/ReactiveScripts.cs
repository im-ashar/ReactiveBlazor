using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ReactiveBlazor;

/// <summary>
/// Renders the antiforgery CSRF meta tag, the dispatch endpoint meta tag, and the
/// ReactiveBlazor script tags into the page head.
/// Place once in your root component (<c>App.razor</c>):
/// <code>
/// &lt;ReactiveScripts /&gt;
/// </code>
/// </summary>
public sealed class ReactiveScripts : ComponentBase
{
    [Inject] internal IAntiforgery Antiforgery { get; set; } = default!;
    [Inject] internal IHttpContextAccessor Http { get; set; } = default!;
    [Inject] internal IOptions<ReactiveOptions> Options { get; set; } = default!;

    /// <summary>
    /// Override the path to <c>reactive.js</c>. Defaults to the RCL content path.
    /// </summary>
    [Parameter]
    public string ScriptPath { get; set; } = "/_content/ReactiveBlazor/reactive.js";

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var token = Antiforgery.GetAndStoreTokens(Http.HttpContext!).RequestToken;

        // CSRF token meta tag
        builder.OpenElement(0, "meta");
        builder.AddAttribute(1, "name", "reactive-csrf");
        builder.AddAttribute(2, "content", token);
        builder.CloseElement();

        // Dispatch endpoint meta tag (so JS knows where to POST)
        builder.OpenElement(3, "meta");
        builder.AddAttribute(4, "name", "reactive-endpoint");
        builder.AddAttribute(5, "content", Options.Value.DispatchPath);
        builder.CloseElement();

        // Library version meta tag (consumed by reactive.js)
        builder.OpenElement(6, "meta");
        builder.AddAttribute(7, "name", "reactive-version");
        builder.AddAttribute(8, "content", ReactiveBlazorVersion.Current);
        builder.CloseElement();

        // Idiomorph (must load before reactive.js)
        builder.OpenElement(9, "script");
        builder.AddAttribute(10, "src", "/_content/ReactiveBlazor/idiomorph.min.js");
        builder.CloseElement();

        // ReactiveBlazor client runtime
        builder.OpenElement(11, "script");
        builder.AddAttribute(12, "src", ScriptPath);
        builder.AddAttribute(13, "defer", true);
        builder.CloseElement();
    }
}

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;

namespace ReactiveBlazor;

/// <summary>
/// Renders the antiforgery CSRF meta tag and the <c>reactive.js</c> script tag into the page head.
/// Place once in your root component (<c>App.razor</c>):
/// <code>
/// &lt;ReactiveScripts /&gt;
/// </code>
/// </summary>
public sealed class ReactiveScripts : ComponentBase
{
    [Inject] internal IAntiforgery Antiforgery { get; set; } = default!;
    [Inject] internal IHttpContextAccessor Http { get; set; } = default!;

    /// <summary>
    /// Override the path to <c>reactive.js</c>. Defaults to the RCL content path.
    /// </summary>
    [Parameter]
    public string ScriptPath { get; set; } = "/_content/ReactiveBlazor/reactive.js";

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var token = Antiforgery.GetAndStoreTokens(Http.HttpContext!).RequestToken;

        builder.OpenElement(0, "meta");
        builder.AddAttribute(1, "name", "reactive-csrf");
        builder.AddAttribute(2, "content", token);
        builder.CloseElement();

        builder.OpenElement(3, "script");
        builder.AddAttribute(4, "src", ScriptPath);
        builder.AddAttribute(5, "defer", true);
        builder.CloseElement();
    }
}

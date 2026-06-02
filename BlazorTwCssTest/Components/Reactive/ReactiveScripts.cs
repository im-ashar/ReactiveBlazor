using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;

namespace ReactiveBlazor;

/// <summary>
/// Place once in your layout head: &lt;ReactiveBlazor.ReactiveScripts /&gt;
/// Emits the antiforgery request token (the JS runtime sends it back as a header) and loads reactive.js.
/// Be sure Idiomorph is loaded too (see README).
/// </summary>
public sealed class ReactiveScripts : ComponentBase
{
    [Inject] public IAntiforgery Antiforgery { get; set; } = default!;
    [Inject] public IHttpContextAccessor Http { get; set; } = default!;

    /// <summary>Where reactive.js is served. For an RCL package this is the _content path.</summary>
    [Parameter] public string ScriptPath { get; set; } = "/_content/ReactiveBlazor/reactive.js";

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

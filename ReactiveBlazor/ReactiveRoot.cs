using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ReactiveBlazor;

/// <summary>
/// Wraps a reactive component's markup in a <c>&lt;div&gt;</c> carrying the signed state
/// envelope and a stable identity. This is the boundary the client-side JS runtime targets
/// for event handling and DOM morphing.
/// </summary>
/// <remarks>
/// Usage inside a <see cref="ReactiveComponent"/> subclass:
/// <code>
/// @inherits ReactiveBlazor.ReactiveComponent
/// &lt;ReactiveRoot Owner="this"&gt;
///     ...your markup...
/// &lt;/ReactiveRoot&gt;
/// </code>
/// </remarks>
public sealed class ReactiveRoot : ComponentBase
{
    /// <summary>The owning <see cref="ReactiveComponent"/> whose state to serialize.</summary>
    [Parameter, EditorRequired]
    public ReactiveComponent Owner { get; set; } = default!;

    /// <summary>The child content to render inside the reactive boundary.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Inject] internal IReactiveStateCodec Codec { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var state = Codec.Protect(Owner.GetType(), Owner.SerializeState());

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "id", Owner.ComponentId);
        builder.AddAttribute(2, "data-component", Owner.GetType().Name);
        builder.AddAttribute(3, "data-state", state);

        var redirectUrl = Owner.RedirectUrl;
        if (string.IsNullOrEmpty(redirectUrl) && Navigation is ReactiveNavigationManager rnm)
        {
            redirectUrl = rnm.RedirectUri;
        }

        if (!string.IsNullOrEmpty(redirectUrl))
            builder.AddAttribute(4, "data-redirect", redirectUrl);

        builder.AddContent(5, ChildContent);
        builder.CloseElement();
    }
}

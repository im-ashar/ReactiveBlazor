using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ReactiveBlazor;

/// <summary>
/// Wraps a component's markup in a &lt;div&gt; carrying the signed state envelope and a stable id.
/// This is the boundary the JS runtime targets and morphs. Usage:
///
///   @inherits ReactiveBlazor.ReactiveComponent
///   &lt;ReactiveBlazor.ReactiveRoot Owner="this"&gt;
///       ...your markup...
///   &lt;/ReactiveBlazor.ReactiveRoot&gt;
///
/// It runs identically on the initial page render and on every re-render from the dispatch endpoint,
/// so the embedded state is always current.
/// </summary>
public sealed class ReactiveRoot : ComponentBase
{
    [Parameter, EditorRequired] public ReactiveComponent Owner { get; set; } = default!;
    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Inject] public IReactiveStateCodec Codec { get; set; } = default!;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var state = Codec.Protect(Owner.GetType(), Owner.SerializeState());

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "id", Owner.ComponentId);
        builder.AddAttribute(2, "data-component", Owner.GetType().Name);
        builder.AddAttribute(3, "data-state", state);
        builder.AddContent(4, ChildContent);
        builder.CloseElement();
    }
}

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

    /// <summary>
    /// The name of the <see cref="ReactiveActionAttribute"/> action to invoke on each poll tick.
    /// Polling is enabled only when this is non-empty <b>and</b> <see cref="PollInterval"/> is greater than zero.
    /// </summary>
    [Parameter]
    public string? PollAction { get; set; }

    /// <summary>
    /// Poll interval in milliseconds. <c>0</c> (the default) disables polling. Bind this to a state
    /// property to start/stop polling at runtime — when it returns to <c>0</c> the poll attributes
    /// disappear on the next morph and the client clears the timer. The client enforces a 250ms floor.
    /// </summary>
    [Parameter]
    public int PollInterval { get; set; }

    /// <summary>
    /// Optional arguments for the poll action, as a pre-serialized JSON array string
    /// (e.g. <c>"[1, \"hello\"]"</c>) — mirrors how <c>data-args</c> is authored.
    /// </summary>
    [Parameter]
    public string? PollArgs { get; set; }

    [Inject] internal IReactiveStateCodec Codec { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Component-level authorization denied (decided in ReactiveComponent.SetParametersAsync).
        // Emit the boundary div so the client morph still has a target by id (wiping any previously
        // rendered authorized content), but with NO state token and NO child content — nothing
        // sensitive leaves the server and the component cannot be re-dispatched.
        if (Owner.IsAuthorizationDenied)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "id", Owner.ComponentId);
            builder.AddAttribute(2, "data-component", Owner.GetType().Name);
            builder.AddAttribute(3, "data-reactive-denied", "");
            builder.CloseElement();
            return;
        }

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

        // Polling attributes (fixed sequence numbers so Blazor's diff handles conditional presence).
        // When PollInterval drops to 0 (or PollAction is cleared) these vanish on the next morph,
        // and the client-side reconciler stops the timer.
        if (!string.IsNullOrEmpty(PollAction) && PollInterval > 0)
        {
            builder.AddAttribute(6, "data-poll", PollAction);
            builder.AddAttribute(7, "data-poll-interval", PollInterval);
            if (!string.IsNullOrEmpty(PollArgs))
                builder.AddAttribute(8, "data-poll-args", PollArgs);
        }

        builder.AddContent(5, ChildContent);
        builder.CloseElement();
    }
}

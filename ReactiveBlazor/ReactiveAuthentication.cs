using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;

namespace ReactiveBlazor;

/// <summary>
/// An <see cref="AuthenticationStateProvider"/> seeded from the current request's
/// <see cref="ClaimsPrincipal"/>. Registered into the dispatch renderer's service provider so that
/// components rendered via <c>HtmlRenderer</c> (which run outside the normal SSR pipeline) still see
/// the authenticated user through standard Blazor authorization primitives.
/// </summary>
internal sealed class SeededAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly Task<AuthenticationState> _state;

    public SeededAuthenticationStateProvider(ClaimsPrincipal user) =>
        _state = Task.FromResult(new AuthenticationState(user));

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;
}

/// <summary>
/// A transparent host component used during reactive dispatch to cascade the
/// <see cref="Task{AuthenticationState}"/> around the component being rendered, so that
/// <c>&lt;AuthorizeView&gt;</c> and <c>[CascadingParameter] Task&lt;AuthenticationState&gt;</c> work
/// exactly as they do under <c>CascadingAuthenticationState</c> in normal SSR.
/// </summary>
/// <remarks>
/// <see cref="CascadingValue{TValue}"/> renders no DOM element, so the wrapped component's
/// <see cref="ReactiveRoot"/> <c>&lt;div&gt;</c> remains the outermost element and the
/// dispatch endpoint's boundary validation still passes.
/// </remarks>
internal sealed class ReactiveAuthHost : ComponentBase
{
    /// <summary>The reactive component type to render inside the auth cascade.</summary>
    [Parameter] public Type ChildType { get; set; } = default!;

    /// <summary>Parameters forwarded to the child component (e.g. <c>ReactiveState</c>, <c>ReactiveAction</c>).</summary>
    [Parameter] public IReadOnlyDictionary<string, object?> ChildParameters { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthProvider { get; set; } = default!;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
        builder.AddAttribute(1, nameof(CascadingValue<Task<AuthenticationState>>.Value),
            AuthProvider.GetAuthenticationStateAsync());
        builder.AddAttribute(2, nameof(CascadingValue<Task<AuthenticationState>>.ChildContent),
            (RenderFragment)(childBuilder =>
            {
                childBuilder.OpenComponent(0, ChildType);
                foreach (var (name, value) in ChildParameters)
                    childBuilder.AddAttribute(1, name, value);
                childBuilder.CloseComponent();
            }));
        builder.CloseComponent();
    }
}

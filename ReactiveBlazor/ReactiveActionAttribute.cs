namespace ReactiveBlazor;

/// <summary>
/// Marks a public method on a <see cref="ReactiveComponent"/> subclass as invokable from the client.
/// Only methods decorated with this attribute can be triggered via <c>data-on-click</c>,
/// <c>data-on-change</c>, or other <c>data-on-*</c> attributes.
/// </summary>
/// <remarks>
/// <para>
/// This is a security boundary: without this attribute, the method cannot be called remotely,
/// even if it is <c>public</c>. This prevents accidental exposure of helper methods, lifecycle
/// overrides, or framework-inherited members.
/// </para>
/// <example>
/// <code>
/// [ReactiveAction]
/// public void Increment() => Count++;
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ReactiveActionAttribute : Attribute
{
    /// <summary>
    /// If set to <c>true</c>, requires a one-time token (nonce) to execute the action.
    /// This prevents replay attacks on non-idempotent actions.
    /// </summary>
    public bool RequireOneTimeToken { get; set; } = false;
}

namespace ReactiveBlazor;

/// <summary>
/// Configuration options for the ReactiveBlazor framework.
/// </summary>
public sealed class ReactiveOptions
{
    /// <summary>
    /// Maximum allowed size (in bytes) for the serialized component state before encryption.
    /// Requests exceeding this limit are rejected with a <c>400 Bad Request</c>.
    /// Default: 64 KB.
    /// </summary>
    public int MaxStateBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Maximum allowed size (in bytes) for the encrypted state token received from the client.
    /// Tokens exceeding this limit are rejected <em>before</em> decryption to prevent
    /// denial-of-service via oversized payloads.
    /// Default: 256 KB (encryption and Base64 inflate the raw state).
    /// </summary>
    public int MaxTokenBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// How long a state token remains valid after it was issued.
    /// Tokens older than this are rejected and the component resets to default state.
    /// Set to <see cref="TimeSpan.Zero"/> to disable expiration (not recommended).
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan StateTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of components a single dispatch request may carry.
    /// Requests exceeding this limit are rejected with a <c>400 Bad Request</c>
    /// <em>before</em> any decryption work is done, to bound the cost of a single request.
    /// Default: 100.
    /// </summary>
    public int MaxComponentsPerDispatch { get; set; } = 100;

    /// <summary>
    /// The URL path where the reactive dispatch endpoint is mapped.
    /// This value is used by both the server-side endpoint and the client-side JS runtime
    /// (emitted via a <c>&lt;meta name="reactive-endpoint"&gt;</c> tag).
    /// Default: <c>/_reactive/dispatch</c>.
    /// </summary>
    public string DispatchPath { get; set; } = "/_reactive/dispatch";

    /// <summary>
    /// If set to <c>true</c>, only public read/write properties decorated with <see cref="ReactiveStateAttribute"/>
    /// are serialized and round-tripped as component state. Properties without the attribute are ignored.
    /// If <c>false</c>, all public read/write properties are serialized unless explicitly ignored.
    /// Default: <c>false</c> (opt-out model).
    /// </summary>
    public bool RequireOptInState { get; set; } = false;

    /// <summary>
    /// When <c>true</c> (the default), a dispatch that returns <c>401 Unauthorized</c> (the user's
    /// session/cookie expired or they are not authenticated) causes the client runtime to stop
    /// polling and perform a full-page reload of the current URL. The app's normal ASP.NET Core
    /// authentication pipeline then issues its configured login redirect (with <c>returnUrl</c>).
    /// Set to <c>false</c> to instead surface a <c>reactive:error</c> event for the app to handle.
    /// </summary>
    public bool ReloadOnUnauthorized { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, each state token is cryptographically bound to the identity of the user it
    /// was issued to (a hash of the authenticated user's stable id claim). On the next dispatch the
    /// token is only accepted for the <em>same</em> user; if a different user replays it — e.g. a
    /// token copied from another session via a shared/kiosk machine, screen share, or a support
    /// attachment — the component silently resets to default state instead of loading the original
    /// user's data. Anonymous users share a single "no user" binding.
    /// <para>
    /// This closes a cross-user <em>state-data</em> replay vector. It does not affect authorization
    /// (every dispatch is already re-authorized against the live <c>HttpContext.User</c>), so leaving
    /// it off never enables privilege escalation.
    /// </para>
    /// <para>
    /// Default: <c>false</c>. Enabling it changes the token format and means tokens stop working when
    /// the user signs in, out, or switches accounts (the component resets) — desirable for
    /// authenticated apps, unnecessary for fully anonymous ones. Overhead is negligible: one short
    /// hash computed once per request and reused across every component on the page.
    /// </para>
    /// </summary>
    public bool BindStateToUser { get; set; } = false;
}

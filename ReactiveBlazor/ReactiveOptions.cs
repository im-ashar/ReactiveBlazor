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
    /// The URL path where the reactive dispatch endpoint is mapped.
    /// This value is used by both the server-side endpoint and the client-side JS runtime
    /// (emitted via a <c>&lt;meta name="reactive-endpoint"&gt;</c> tag).
    /// Default: <c>/_reactive/dispatch</c>.
    /// </summary>
    public string DispatchPath { get; set; } = "/_reactive/dispatch";
}

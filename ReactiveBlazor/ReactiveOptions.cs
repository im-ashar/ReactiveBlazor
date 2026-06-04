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
    /// The URL path where the reactive dispatch endpoint is mapped.
    /// This value is used by both the server-side endpoint and the client-side JS runtime
    /// (emitted via a <c>&lt;meta name="reactive-endpoint"&gt;</c> tag).
    /// Default: <c>/_reactive/dispatch</c>.
    /// </summary>
    public string DispatchPath { get; set; } = "/_reactive/dispatch";
}

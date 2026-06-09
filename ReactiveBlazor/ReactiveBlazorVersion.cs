using System.Reflection;

namespace ReactiveBlazor;

/// <summary>
/// Exposes the ReactiveBlazor library version at runtime.
/// The value is sourced from the assembly metadata, which is populated from
/// the <c>ReactiveBlazorVersion</c> MSBuild property defined in
/// <c>Directory.Build.props</c> at the repository root.
/// </summary>
public static class ReactiveBlazorVersion
{
    /// <summary>
    /// The current library version (e.g. <c>"1.3.0"</c>), without any
    /// SourceLink commit-hash suffix.
    /// </summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(ReactiveBlazorVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

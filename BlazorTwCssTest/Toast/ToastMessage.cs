namespace BlazorTwCssTest.Toast;

/// <summary>
/// Represents a toast notification message.
/// Copy this file + ToastContainer.razor to reuse in other projects.
/// </summary>
public record ToastMessage(string Message, ToastType Type = ToastType.Success);

public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}

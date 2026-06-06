namespace ReactiveBlazor.Demo.Models;

public sealed class Notification
{
    public int Id { get; init; }
    public string Level { get; init; } = "info"; // info | success | warning
    public string Message { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

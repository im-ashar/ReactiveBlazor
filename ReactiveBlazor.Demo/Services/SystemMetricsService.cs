namespace ReactiveBlazor.Demo.Services;

/// <summary>
/// Fake "server telemetry" source for the polling demo. Each call to <see cref="Sample"/>
/// returns fresh, randomly drifting values — the point being that the data changes on its
/// own between polls, with no user interaction. Process-wide for simplicity.
/// </summary>
public sealed class SystemMetricsService
{
    private readonly Random _rng = new();
    private double _cpu = 35;       // %
    private double _memory = 2048;  // MB
    private int _requests = 120;    // req/s

    /// <summary>The latest snapshot of (fake) system metrics.</summary>
    public readonly record struct Sample(double Cpu, double MemoryMb, int RequestsPerSec, DateTime At);

    /// <summary>Drifts each metric by a small random amount and returns the new snapshot.</summary>
    public Sample Read()
    {
        _cpu = Clamp(_cpu + _rng.Next(-8, 9), 2, 99);
        _memory = Clamp(_memory + _rng.Next(-128, 129), 512, 8192);
        _requests = (int)Clamp(_requests + _rng.Next(-25, 26), 0, 1000);
        return new Sample(Math.Round(_cpu, 1), Math.Round(_memory), _requests, DateTime.Now);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}

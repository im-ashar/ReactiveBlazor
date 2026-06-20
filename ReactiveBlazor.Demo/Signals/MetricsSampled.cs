using ReactiveBlazor;

namespace ReactiveBlazor.Demo.Signals;

/// <summary>Published on every poll tick of the metrics dashboard so OOB subscribers can react.</summary>
/// <param name="Cpu">The CPU percentage from the latest sample.</param>
/// <param name="At">When the sample was taken.</param>
public sealed record MetricsSampled(double Cpu, DateTime At) : IReactiveSignal;

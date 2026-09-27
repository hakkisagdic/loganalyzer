namespace Bizigo.Contracts;

/// <summary>Canonical evidence inputs: union of two half-open windows, without policy filters.</summary>
public sealed record TelemetryInputWindow(TelemetrySignal Signal, decimal EventFrom, decimal EventTo,
    decimal BaselineFrom, decimal BaselineTo, IReadOnlyList<string> SourceIds)
{
    public void Validate()
    {
        new TelemetryQuery { Signal = Signal, FromNano = EventFrom, ToNano = EventTo }.Validate();
        new TelemetryQuery { Signal = Signal, FromNano = BaselineFrom, ToNano = BaselineTo }.Validate();
        if (SourceIds.Count > 256 || SourceIds.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1024))
            throw new ArgumentException("Invalid source filter.");
    }
}

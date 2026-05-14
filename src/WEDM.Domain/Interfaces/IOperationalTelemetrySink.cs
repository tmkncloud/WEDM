namespace WEDM.Domain.Interfaces;

/// <summary>
/// Future hook for deployment analytics / metrics exporters. Default implementation is a no-op.
/// </summary>
public interface IOperationalTelemetrySink
{
    void RecordEvent(string eventName, IReadOnlyDictionary<string, string>? properties = null);
}

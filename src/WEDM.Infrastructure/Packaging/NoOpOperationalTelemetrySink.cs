using WEDM.Domain.Interfaces;

namespace WEDM.Infrastructure.Packaging;

public sealed class NoOpOperationalTelemetrySink : IOperationalTelemetrySink
{
    public void RecordEvent(string eventName, IReadOnlyDictionary<string, string>? properties = null)
    {
        _ = eventName;
        _ = properties;
    }
}

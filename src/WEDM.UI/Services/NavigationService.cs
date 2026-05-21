using WEDM.UI.Models;

namespace WEDM.UI.Services;

/// <summary>
/// Simple, thread-safe shell navigation service.
/// Fires SectionChanged on the calling thread — callers that update UI
/// must already be on the dispatcher, which AppShellViewModel handles.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private AppSection _current = AppSection.Dashboard;

    public AppSection CurrentSection => _current;

    public event EventHandler<AppSection>? SectionChanged;

    public void NavigateTo(AppSection section)
    {
        if (_current == section) return;
        _current = section;
        SectionChanged?.Invoke(this, section);
    }
}

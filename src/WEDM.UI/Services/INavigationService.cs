using WEDM.UI.Models;

namespace WEDM.UI.Services;

/// <summary>
/// Shell navigation contract. Allows any ViewModel to trigger section
/// changes without coupling to the shell ViewModel directly.
/// </summary>
public interface INavigationService
{
    /// <summary>Gets the currently active section.</summary>
    AppSection CurrentSection { get; }

    /// <summary>Fires when the active section changes.</summary>
    event EventHandler<AppSection>? SectionChanged;

    /// <summary>Activates the given section.</summary>
    void NavigateTo(AppSection section);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEDM.UI.Models;
using WEDM.UI.Services;

namespace WEDM.UI.ViewModels;

/// <summary>
/// Represents a single slot in the ActivityBar (48×48 icon button).
/// Drives the active 3px left-accent border via IsActive.
/// </summary>
public sealed partial class ActivityBarItemViewModel : ObservableObject
{
    private readonly INavigationService _nav;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _hasBadge;

    [ObservableProperty]
    private string _badgeCount = string.Empty;

    public AppSection Section { get; }

    /// <summary>Segoe MDL2 Assets glyph character.</summary>
    public string IconGlyph { get; }

    public string Tooltip { get; }

    public ActivityBarItemViewModel(AppSection section, string iconGlyph, string tooltip, INavigationService nav)
    {
        Section = section;
        IconGlyph = iconGlyph;
        Tooltip = tooltip;
        _nav = nav;
    }

    [RelayCommand]
    private void Activate()
    {
        _nav.NavigateTo(Section);
    }
}

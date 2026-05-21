using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WEDM.UI.ViewModels.Runtime;

/// <summary>
/// A single transient notification entry shown in the runtime notification bar.
/// Carries operation feedback, health alerts, and discovery results.
/// </summary>
public sealed partial class RuntimeNotificationViewModel : ObservableObject
{
    public enum NotificationKind { Info, Success, Warning, Error }

    [ObservableProperty] private bool _isDismissed;

    public string          Message   { get; }
    public NotificationKind Kind     { get; }
    public DateTimeOffset  Timestamp { get; } = DateTimeOffset.Now;
    public string          Source    { get; }

    /// <summary>Friendly time display: "just now", "2m ago", etc.</summary>
    public string TimeAgo
    {
        get
        {
            var delta = DateTimeOffset.Now - Timestamp;
            if (delta.TotalSeconds < 10)  return "just now";
            if (delta.TotalMinutes < 1)   return $"{(int)delta.TotalSeconds}s ago";
            if (delta.TotalHours   < 1)   return $"{(int)delta.TotalMinutes}m ago";
            return Timestamp.ToString("HH:mm");
        }
    }

    public RuntimeNotificationViewModel(string message, NotificationKind kind, string source = "Runtime")
    {
        Message = message;
        Kind    = kind;
        Source  = source;
    }

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;
}

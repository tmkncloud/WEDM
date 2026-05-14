using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WEDM.UI.ViewModels.Base;

/// <summary>
/// Base ViewModel for all WEDM view models.
/// Uses CommunityToolkit.Mvvm for source-generated INotifyPropertyChanged,
/// commands, and observable properties — zero boilerplate.
///
/// Architecture notes:
///   • All observable properties use [ObservableProperty] source generation
///   • Commands use [RelayCommand] with async support and cancellation
///   • Error handling is centralised through HandleException()
///   • IsBusy / IsLoading are provided at base level for consistent UI state
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = "Please wait...";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _title = string.Empty;

    public bool IsNotBusy => !IsBusy;

    protected void SetBusy(bool busy, string message = "Please wait...")
    {
        IsBusy      = busy;
        BusyMessage = message;
        if (busy) ClearError();
    }

    protected void SetError(string message)
    {
        ErrorMessage = message;
        HasError     = true;
        IsBusy       = false;
    }

    protected void ClearError()
    {
        ErrorMessage = null;
        HasError     = false;
    }

    protected void HandleException(Exception ex, string? context = null)
    {
        var prefix = context is not null ? $"[{context}] " : string.Empty;
        SetError($"{prefix}{ex.Message}");
    }

    /// <summary>
    /// Run an action on the UI thread — safe to call from any thread.
    /// </summary>
    protected static void DispatchUI(Action action)
        => System.Windows.Application.Current.Dispatcher.Invoke(action);

    /// <summary>
    /// Called when the view is first shown — override for async initialisation.
    /// </summary>
    public virtual Task InitialiseAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when navigating away from this view.
    /// </summary>
    public virtual Task CleanupAsync() => Task.CompletedTask;
}

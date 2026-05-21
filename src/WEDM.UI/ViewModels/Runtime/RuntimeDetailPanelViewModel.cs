using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Runtime;

/// <summary>
/// Right-side detail panel for a selected RuntimeComponent.
/// Shows identity, health, paths, action history, and hosts the LogViewerViewModel.
/// </summary>
public sealed partial class RuntimeDetailPanelViewModel : ObservableObject
{
    private readonly Func<RuntimeComponent, CancellationToken, IAsyncEnumerable<LogTailEntry>> _tailFunc;

    // ── Bound component ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComponent))]
    [NotifyPropertyChangedFor(nameof(ComponentName))]
    [NotifyPropertyChangedFor(nameof(DomainName))]
    [NotifyPropertyChangedFor(nameof(KindDisplay))]
    [NotifyPropertyChangedFor(nameof(Host))]
    [NotifyPropertyChangedFor(nameof(Port))]
    [NotifyPropertyChangedFor(nameof(LogFile))]
    [NotifyPropertyChangedFor(nameof(StartScript))]
    [NotifyPropertyChangedFor(nameof(ConsoleUrl))]
    [NotifyPropertyChangedFor(nameof(HasConsoleUrl))]
    [NotifyPropertyChangedFor(nameof(DomainHome))]
    private RuntimeComponentViewModel? _component;

    // ── Tab selection ─────────────────────────────────────────────────────────

    [ObservableProperty] private int _selectedTabIndex = 0;  // 0=Details 1=Logs 2=History

    // ── Operation state ───────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isOperationRunning;
    [ObservableProperty] private string _operationStatusText = string.Empty;

    // ── Sub-VMs ───────────────────────────────────────────────────────────────

    public LogViewerViewModel LogViewer { get; } = new();

    /// <summary>Recent operation history (last 20 results).</summary>
    public ObservableCollection<OperationHistoryEntry> OperationHistory { get; } = [];

    // ── Computed projections from Component ───────────────────────────────────

    public bool   HasComponent    => Component is not null;
    public string ComponentName   => Component?.Name        ?? string.Empty;
    public string DomainName      => Component?.DomainName  ?? string.Empty;
    public string KindDisplay     => Component?.KindDisplay ?? string.Empty;
    public string Host            => Component?.Source.Host ?? string.Empty;
    public int    Port            => Component?.Source.Port ?? 0;
    public string LogFile         => Component?.Source.LogFile    ?? string.Empty;
    public string StartScript     => Component?.Source.StartScript ?? string.Empty;
    public string ConsoleUrl      => Component?.Source.ConsoleUrl ?? string.Empty;
    public bool   HasConsoleUrl   => !string.IsNullOrEmpty(ConsoleUrl);
    public string DomainHome      => Component?.Source.DomainHome ?? string.Empty;

    public RuntimeDetailPanelViewModel(
        Func<RuntimeComponent, CancellationToken, IAsyncEnumerable<LogTailEntry>> tailFunc)
    {
        _tailFunc = tailFunc;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Binds to a new component. Stops any running log tail.</summary>
    public void SetComponent(RuntimeComponentViewModel? vm)
    {
        LogViewer.StopTail();
        Component = vm;
    }

    /// <summary>Records a completed operation in the history ring.</summary>
    public void RecordOperation(RuntimeControlResult result)
    {
        var entry = new OperationHistoryEntry(result);
        if (OperationHistory.Count >= 20)
            OperationHistory.RemoveAt(OperationHistory.Count - 1);
        OperationHistory.Insert(0, entry);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void StartLogTail()
    {
        if (Component is null) return;
        SelectedTabIndex = 1;  // switch to Logs tab
        LogViewer.StartTail(Component.Source, _tailFunc);
    }

    [RelayCommand]
    private void StopLogTail() => LogViewer.StopTail();

    [RelayCommand]
    private void OpenConsole()
    {
        var url = ConsoleUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url, UseShellExecute = true
            });
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>A single entry in the operation history ring.</summary>
public sealed class OperationHistoryEntry
{
    public string        Operation  { get; }
    public bool          Succeeded  { get; }
    public string        Duration   { get; }
    public string        Error      { get; }
    public string        Timestamp  { get; }
    public RuntimeState  FinalState { get; }

    public OperationHistoryEntry(RuntimeControlResult r)
    {
        Operation  = r.Operation;
        Succeeded  = r.Succeeded;
        Duration   = $"{r.Duration.TotalSeconds:F1}s";
        Error      = r.Error ?? string.Empty;
        Timestamp  = r.StartedAt.ToLocalTime().ToString("HH:mm:ss");
        FinalState = r.FinalState;
    }
}

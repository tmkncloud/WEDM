using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Runtime;

/// <summary>
/// Manages the virtualized live log viewer for a single RuntimeComponent.
///
/// Features:
///   • Tail mode (auto-scroll) with pause
///   • Max 3000 lines — oldest lines evicted to keep UI smooth
///   • Per-line severity colour via IsError / IsWarning flags
///   • Filter text — hides non-matching lines (updates LogLines in-place)
///   • Source switching — replaces log stream when component changes
/// </summary>
public sealed partial class LogViewerViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 3000;

    [ObservableProperty] private bool   _isTailing     = false;
    [ObservableProperty] private bool   _isPaused      = false;
    [ObservableProperty] private string _filterText    = string.Empty;
    [ObservableProperty] private string _sourceLabel   = "No component selected";
    [ObservableProperty] private int    _totalLines    = 0;
    [ObservableProperty] private bool   _hasNewErrors  = false;
    [ObservableProperty] private string _statusText    = "Select a component and click Tail Log";

    /// <summary>Filtered log lines shown in the viewer.</summary>
    public ObservableCollection<LogLineViewModel> LogLines { get; } = [];

    // Internal full list (filter operates on this)
    private readonly List<LogLineViewModel> _allLines = [];

    private CancellationTokenSource? _tailCts;
    private RuntimeComponent?        _component;

    public event Action? ScrollToBottomRequested;

    public LogViewerViewModel() { }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Clear()
    {
        _allLines.Clear();
        Dispatch(() => LogLines.Clear());
        TotalLines   = 0;
        HasNewErrors = false;
        StatusText   = "Log cleared.";
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused   = !IsPaused;
        StatusText = IsPaused ? "Paused — new lines buffered" : "Live — tailing log";
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts tailing the log for the given component.
    /// Cancels any existing tail first.
    /// </summary>
    public void StartTail(RuntimeComponent component,
        Func<RuntimeComponent, CancellationToken, IAsyncEnumerable<LogTailEntry>> tailFunc)
    {
        StopTail();
        _component  = component;
        SourceLabel = $"{component.Name} — {Path.GetFileName(component.LogFile)}";
        StatusText  = "Connecting to log…";
        IsTailing   = true;
        IsPaused    = false;

        _tailCts = new CancellationTokenSource();
        var token = _tailCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in tailFunc(component, token))
                {
                    if (token.IsCancellationRequested) break;
                    if (!IsPaused) AppendLine(entry);
                }
            }
            catch (OperationCanceledException) { /* normal stop */ }
            catch (Exception ex)
            {
                AppendRaw($"[LogViewer] Stream error: {ex.Message}", isError: true);
            }
            finally
            {
                Dispatch(() =>
                {
                    IsTailing  = false;
                    StatusText = "Log stream ended.";
                });
            }
        }, token);
    }

    /// <summary>Stops the current log tail.</summary>
    public void StopTail()
    {
        _tailCts?.Cancel();
        _tailCts?.Dispose();
        _tailCts   = null;
        IsTailing  = false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void AppendLine(LogTailEntry entry)
    {
        var vm = new LogLineViewModel(entry);
        Dispatch(() =>
        {
            _allLines.Add(vm);
            TotalLines = _allLines.Count;

            // Evict oldest lines when over limit
            if (_allLines.Count > MaxLogLines)
            {
                var evict = _allLines.Count - MaxLogLines;
                _allLines.RemoveRange(0, evict);
                // Rebuild filtered list
                ApplyFilter();
            }
            else if (MatchesFilter(vm))
            {
                LogLines.Add(vm);
                if (!IsPaused)
                    ScrollToBottomRequested?.Invoke();
            }

            if (vm.IsError)
                HasNewErrors = true;

            StatusText = $"Live — {TotalLines} lines";
        });
    }

    private void AppendRaw(string text, bool isError = false)
    {
        AppendLine(new LogTailEntry { Line = text, IsError = isError, Source = "System" });
    }

    private void ApplyFilter()
    {
        Dispatch(() =>
        {
            LogLines.Clear();
            var filter = FilterText.Trim();
            foreach (var line in _allLines)
            {
                if (MatchesFilter(line, filter))
                    LogLines.Add(line);
            }
        });
    }

    private bool MatchesFilter(LogLineViewModel vm, string? filter = null)
    {
        filter ??= FilterText.Trim();
        if (string.IsNullOrEmpty(filter)) return true;
        return vm.Line.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void Dispatch(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) action();
        else disp.Invoke(action);
    }

    public void Dispose()
    {
        StopTail();
    }
}

/// <summary>
/// A single log line view model — lightweight value object for the virtualized list.
/// </summary>
public sealed class LogLineViewModel
{
    public string        Line      { get; }
    public bool          IsError   { get; }
    public bool          IsWarning { get; }
    public string        Source    { get; }
    public string        TimeLabel { get; }
    public DateTimeOffset Timestamp { get; }

    private static readonly string[] WarningMarkers = ["WARN", "Warning", "WARNING", "BEA-0"];

    public LogLineViewModel(LogTailEntry entry)
    {
        Line      = entry.Line;
        IsError   = entry.IsError;
        Source    = entry.Source;
        Timestamp = entry.Timestamp;
        TimeLabel = entry.Timestamp.ToString("HH:mm:ss.fff");
        IsWarning = !IsError && WarningMarkers.Any(m =>
            entry.Line.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}

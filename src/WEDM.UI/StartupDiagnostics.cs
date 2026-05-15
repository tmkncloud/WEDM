using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WEDM.UI.ViewModels;

namespace WEDM.UI;

/// <summary>
/// Bootstrap-time tracing and global exception surfacing (before Serilog host wiring is guaranteed).
/// Writes under ProgramData\WEDM\logs and optional startup-error.txt next to the executable.
/// </summary>
internal static class StartupDiagnostics
{
    private static readonly object FileLock = new();
    private static string? _tracePath;
    private static bool _installed;

    public static string LogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WEDM", "logs");

    private static string TracePath => _tracePath ??= Path.Combine(LogsDirectory, "startup-trace.log");

    private static string StartupErrorPath =>
        Path.Combine(AppContext.BaseDirectory, "startup-error.txt");

    public static void Install(global::System.Windows.Application app)
    {
        if (_installed) return;
        _installed = true;

        try
        {
            Directory.CreateDirectory(LogsDirectory);
        }
        catch
        {
            /* last resort: trace only to base directory */
            _tracePath = Path.Combine(AppContext.BaseDirectory, "startup-trace.log");
        }

        Trace("StartupDiagnostics", "Global handlers installed");
        TryEnableBindingTrace();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var msg = $"AppDomain.UnhandledException isTerminating={args.IsTerminating}";
            var detail = ex?.ToString() ?? args.ExceptionObject?.ToString() ?? msg;
            Trace("AppDomain.UnhandledException", detail);
            TryWriteStartupError("AppDomain.UnhandledException", ex ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));

            // Only attempt dialog when not already terminating (avoids re-entrance during CLR fatal path).
            if (!args.IsTerminating)
            {
                try
                {
                    global::System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        ShowUiExceptionDialog("Unhandled application error", ex ?? new Exception(detail)));
                }
                catch { /* headless or already shutting down */ }
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Mark observed FIRST so the runtime does not escalate to process termination.
            args.SetObserved();
            Trace("TaskScheduler.UnobservedTaskException", args.Exception?.ToString());
            TryWriteStartupError("UnobservedTaskException", args.Exception);
            // Log to structured sink if already running — best-effort.
            try
            {
                var svc = global::System.Windows.Application.Current?.TryFindResource("LoggingService")
                    as WEDM.Domain.Interfaces.ILoggingService;
                svc?.Error("Unobserved task exception (fire-and-forget leak detected)",
                    args.Exception, "TaskScheduler");
            }
            catch { /* ignore — logging may not be ready */ }
        };

        app.DispatcherUnhandledException += (_, args) =>
        {
            Trace("DispatcherUnhandledException", args.Exception.ToString());
            TryWriteStartupError("DispatcherUnhandledException", args.Exception);
            // Keep the shell alive so the operator can read logs and retry navigation.
            args.Handled = true;
            ShowUiExceptionDialog("Unexpected UI error", args.Exception);
        };
    }

    public static void TraceWizardNavigation(string fromStep, string toStep, string? mode = null)
    {
        var detail = string.IsNullOrWhiteSpace(mode)
            ? $"{fromStep} → {toStep}"
            : $"{fromStep} → {toStep} ({mode})";
        Trace("Wizard.Navigation", detail);
    }

    public static void TraceWizardNavigationFailure(string stepTitle, int stepIndex, Exception ex)
    {
        Trace("Wizard.Navigation.FAILED", $"step='{stepTitle}' index={stepIndex} | {ex.GetType().Name}: {ex.Message}");
        TryWriteStartupError($"Wizard navigation failed at '{stepTitle}'", ex);
    }

    public static void ShowWizardNavigationError(Exception ex, string stepTitle)
        => ShowUiExceptionDialog($"Could not open wizard step: {stepTitle}", ex);

    public static void ShowUiExceptionDialog(string title, Exception ex)
    {
        try
        {
            var inner = ex is System.Windows.Markup.XamlParseException xaml && xaml.InnerException is not null
                ? xaml.InnerException.Message
                : ex.Message;
            global::System.Windows.MessageBox.Show(
                $"{inner}\n\nTechnical details were written to:\n{StartupErrorPath}\nand\n{TracePath}",
                $"WEDM — {title}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            /* headless */
        }
    }

    public static void Trace(string phase, string? detail = null)
    {
        var line = $"{DateTimeOffset.Now:u} | {phase}";
        if (!string.IsNullOrEmpty(detail))
            line += " | " + detail.ReplaceLineEndings(" ");
        line += Environment.NewLine;

        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
                File.AppendAllText(TracePath, line, Encoding.UTF8);
            }
            catch
            {
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-trace-fallback.log"), line, Encoding.UTF8); }
                catch { /* ignore */ }
            }
        }
    }

    public static void HandleStartupFailure(global::System.Windows.Window? splash, Exception ex)
    {
        Trace("STARTUP_FAILURE", ex.ToString());
        TryWriteStartupError("Startup failure", ex);

        try { splash?.Close(); } catch { /* ignore */ }

        try
        {
            global::System.Windows.MessageBox.Show(
                $"WEDM could not start.\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails were written to:\n{StartupErrorPath}\nand\n{TracePath}",
                "WebLogic Enterprise Deployment Manager — startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            /* MessageBox can fail in headless sessions */
        }

        try { global::System.Windows.Application.Current.Shutdown(-1); }
        catch { Environment.Exit(-1); }
    }

    public static void ValidateServiceResolution(IServiceProvider services)
    {
        Trace("DI", "Resolving IWorkflowOrchestrator…");
        _ = services.GetRequiredService<WEDM.Domain.Interfaces.IWorkflowOrchestrator>();

        Trace("DI", "Resolving IStepExecutorFactory…");
        _ = services.GetRequiredService<WEDM.Engine.Workflow.Steps.IStepExecutorFactory>();

        Trace("DI", "Resolving MainWindowViewModel…");
        _ = services.GetRequiredService<MainWindowViewModel>();

        Trace("DI", "Resolving MainWindow…");
        _ = services.GetRequiredService<MainWindow>();

        Trace("DI", "Critical service graph OK");
    }

    private static void TryEnableBindingTrace()
    {
        try
        {
            PresentationTraceSources.DataBindingSource.Switch.Level =
                SourceLevels.Error | SourceLevels.Warning;
            var path = Path.Combine(LogsDirectory, "startup-databinding.log");
            var sw = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(sw));
            Trace("Diagnostics", "WPF DataBinding trace → startup-databinding.log");
        }
        catch (Exception ex)
        {
            Trace("Diagnostics", $"WPF DataBinding trace not enabled: {ex.Message}");
        }
    }

    private static void TryWriteStartupError(string title, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ex.ToString());
            File.WriteAllText(StartupErrorPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(LogsDirectory, "startup-error.txt"),
                    ex.ToString(),
                    Encoding.UTF8);
            }
            catch { /* ignore */ }
        }
    }
}

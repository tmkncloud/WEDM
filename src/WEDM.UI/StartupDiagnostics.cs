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

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var msg = $"AppDomain.UnhandledException isTerminating={args.IsTerminating}";
            Trace("AppDomain.UnhandledException", ex?.ToString() ?? args.ExceptionObject?.ToString() ?? msg);
            TryWriteStartupError("AppDomain.UnhandledException", ex ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace("TaskScheduler.UnobservedTaskException", args.Exception?.ToString());
            TryWriteStartupError("UnobservedTaskException", args.Exception);
        };

        app.DispatcherUnhandledException += (_, args) =>
        {
            Trace("DispatcherUnhandledException", args.Exception.ToString());
            TryWriteStartupError("DispatcherUnhandledException", args.Exception);
            // Let default WPF handling run after we have persisted diagnostics.
            args.Handled = false;
        };
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

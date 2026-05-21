using System.Windows.Controls;
using WEDM.UI.ViewModels.Runtime;

namespace WEDM.UI.Views.Runtime;

/// <summary>
/// Code-behind for RuntimeLogViewerView.
/// Handles auto-scroll-to-bottom when the LogViewerViewModel fires ScrollToBottomRequested.
/// </summary>
public partial class RuntimeLogViewerView : UserControl
{
    private LogViewerViewModel? _vm;

    public RuntimeLogViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.ScrollToBottomRequested -= ScrollToBottom;

        _vm = e.NewValue as LogViewerViewModel;

        if (_vm is not null)
            _vm.ScrollToBottomRequested += ScrollToBottom;
    }

    private void ScrollToBottom()
    {
        if (LogListBox.Items.Count == 0) return;
        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }
}

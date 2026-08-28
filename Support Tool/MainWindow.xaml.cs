using System.ComponentModel;
using System.Windows;
using Support_Tool.ViewModels;

namespace Support_Tool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Phase 2A safety requirement: real WUA search/download/install calls can now actually
    // execute inside Support Tool (the engine was previously only ever ported as dead code), so
    // closing the window mid-operation must not tear down the process underneath a live COM
    // call - that would bypass WindowsUpdateExecutor's own RequestAbort/End*/CleanUp lifecycle
    // entirely. This is the ONLY close-time behavior implemented so far - the full three-tier
    // model (block mid-operation / skip teardown while a resume is pending / normal teardown
    // otherwise) needs session.json and RuntimeCleanupService, both later phases. For now,
    // outside an active operation, closing behaves exactly as it always has.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { SharedState.IsWuaOperationActive: true })
        {
            e.Cancel = true;
            MessageBox.Show(
                "Windows Update is currently running. Please wait until the current operation finishes before closing.",
                "Windows Update in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        base.OnClosing(e);
    }
}

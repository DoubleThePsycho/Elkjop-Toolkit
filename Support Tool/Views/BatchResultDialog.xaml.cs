using System.Windows;
using Support_Tool.Models;

namespace Support_Tool.Views;

// The one shared, informational-only result presentation for a finished Install or Uninstall
// batch - see BatchResultSummary's own remarks. Deliberately just an OK button: this is never
// another confirmation step, the batch has already fully run by the time this is shown.
public partial class BatchResultDialog : Window
{
    public BatchResultDialog(BatchResultSummary summary)
    {
        InitializeComponent();
        DataContext = summary;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}

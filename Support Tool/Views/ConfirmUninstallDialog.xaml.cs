using System.Windows;
using System.Windows.Controls;

namespace Support_Tool.Views;

public partial class ConfirmUninstallDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmUninstallDialog(IReadOnlyList<string> itemNames)
    {
        InitializeComponent();

        CountLabel.Text = $"You are about to PERMANENTLY REMOVE {itemNames.Count} item(s):";
        ItemsListText.Text = string.Join("\n", itemNames.Select(n => $"  •  {n}"));
    }

    private void ConfirmTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = ConfirmTextBox.Text == "UNINSTALL";
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

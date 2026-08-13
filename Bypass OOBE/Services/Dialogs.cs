using System.Windows.Forms;

namespace Bypass_OOBE.Services;

internal static class Dialogs
{
    public static DialogResult Show(
        string text,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information)
        => MessageBox.Show(text, AppInfo.DialogTitle, buttons, icon);
}

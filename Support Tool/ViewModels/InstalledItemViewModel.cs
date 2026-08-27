using System.Windows.Media;
using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.ViewModels;

public class InstalledItemViewModel : ViewModelBase
{
    private readonly InstalledItem _item;
    private bool _isSelected;

    public InstalledItemViewModel(InstalledItem item, RuleEntry? rule)
    {
        _item = item;

        bool blocked = rule?.Blocked ?? false;
        IsEnabled = !blocked;
        HasWarning = rule?.Warn ?? false;

        var parts = new List<string> { item.Name };
        if (item.InstalledSizeKb.HasValue)
            parts.Add(FormatSize(item.InstalledSizeKb.Value));
        if (!string.IsNullOrWhiteSpace(rule?.Reason))
            parts.Add(rule.Reason);
        Tooltip = string.Join("\n", parts);

        _isSelected = !blocked && (rule?.Preselected ?? false);
    }

    public string Name => _item.Name;
    public string Version => _item.Version;
    public string Publisher => _item.Publisher;
    public ImageSource? Icon => _item.Icon;
    public InstalledItem Item => _item;

    public bool IsEnabled { get; }
    public bool HasWarning { get; }
    public string Tooltip { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static string FormatSize(long kb)
    {
        if (kb < 1024) return $"{kb} KB";
        if (kb < 1024 * 1024) return $"{kb / 1024.0:F1} MB";
        return $"{kb / (1024.0 * 1024):F1} GB";
    }
}

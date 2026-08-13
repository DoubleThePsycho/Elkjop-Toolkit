using Support_Tool.Models;

namespace Support_Tool.ViewModels;

public class SharedViewState : ViewModelBase
{
    private bool _isGridView;
#if DEBUG
    private bool _isDryRun = true;
#else
    private readonly bool _isDryRun = false;
#endif

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
                OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !_isGridView;

    public bool IsDryRun
    {
        get => _isDryRun;
#if DEBUG
        set
        {
            if (SetProperty(ref _isDryRun, value))
                OnPropertyChanged(nameof(IsLiveMode));
        }
#endif
    }

    public bool IsLiveMode => !_isDryRun;

#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    public ExecutionMode ExecutionMode => _isDryRun ? ExecutionMode.DryRun : ExecutionMode.Real;
}

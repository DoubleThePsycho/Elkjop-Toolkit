using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using Support_Tool.Models;
using Support_Tool.Services;

namespace Support_Tool.ViewModels;

public class InstallPageViewModel : ViewModelBase
{
    private readonly InstallCatalogService _catalog;
    private readonly WingetStatusChecker _statusChecker;
    private readonly InstallService _installService;
    private readonly AppIconCacheService _iconCache;
    private bool _isLoading;
    private bool _isInstalling;
    private string _statusMessage = "";

    public InstallPageViewModel(
        InstallCatalogService catalog,
        WingetStatusChecker statusChecker,
        InstallService installService,
        AppIconCacheService iconCache,
        SharedViewState viewState)
    {
        _catalog = catalog;
        _statusChecker = statusChecker;
        _installService = installService;
        _iconCache = iconCache;
        ViewState = viewState;

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedCount > 0);
    }

    public SharedViewState ViewState { get; }
    public ObservableCollection<InstallCategoryViewModel> Categories { get; } = new();
    public AsyncRelayCommand InstallCommand { get; }
    public bool HasLoaded { get; private set; }

    public int SelectedCount => Categories.SelectMany(c => c.Items).Count(x => x.IsSelected);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set => SetProperty(ref _isInstalling, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task LoadAsync()
    {
        HasLoaded = true;
        IsLoading = true;

        foreach (var category in Categories)
            foreach (var item in category.Items)
                item.PropertyChanged -= OnItemPropertyChanged;
        Categories.Clear();

        try
        {
            foreach (var category in await _catalog.GetCatalogAsync())
            {
                var itemVms = new List<InstallableAppViewModel>();
                foreach (var app in category.Apps)
                {
                    var state = await _statusChecker.GetStateAsync(app.WingetId);
                    var iconPath = await _iconCache.GetOrFetchIconPathAsync(app.WingetId, app.Link);
                    var vm = new InstallableAppViewModel(app, state, iconPath);
                    vm.PropertyChanged += OnItemPropertyChanged;
                    itemVms.Add(vm);
                }
                Categories.Add(new InstallCategoryViewModel(category.Name, itemVms, category.ShowSelectAll));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifySelectionChanged();
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallableAppViewModel.IsSelected))
            NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        InstallCommand.RaiseCanExecuteChanged();
    }

    private async Task InstallAsync()
    {
        var selected = Categories
            .SelectMany(c => c.Items)
            .Where(x => x.IsSelected)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "No applications are selected.",
                "Nothing Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (ViewState.IsLiveMode)
        {
            var names = string.Join("\n", selected.Select(i => $"• {i.Name}"));
            var confirm = MessageBox.Show(
                $"You are about to install/update {selected.Count} application(s):\n\n{names}\n\nContinue?",
                "Confirm Install",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var confirm = MessageBox.Show(
                $"[DRY RUN]\n\nYou are about to simulate installing/updating {selected.Count} application(s).\n\n" +
                "No software will be installed.\n\nContinue?",
                "Confirm Dry Run",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        IsInstalling = true;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        List<InstallResult> results;
        try
        {
            results = await _installService.InstallAsync(
                selected.Select(x => (x.App, x.State)), ViewState.ExecutionMode, progress);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Install operation failed unexpectedly:\n{ex.Message}",
                "Install Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = "";
            return;
        }
        finally
        {
            IsInstalling = false;
        }

        StatusMessage = "";
        ShowResultSummary(results);
        await LoadAsync();
    }

    private void ShowResultSummary(List<InstallResult> results)
    {
        bool isDryRun = results.Any(r => r.Status == InstallOutcome.DryRun);

        if (isDryRun)
        {
            MessageBox.Show(
                $"Dry-run complete.\n\n{results.Count} item(s) would have been processed.\n" +
                "No real software was installed.",
                "Dry-Run Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var succeeded = results.Where(r => r.Status == InstallOutcome.Succeeded).ToList();
        var failed    = results.Where(r => r.Status == InstallOutcome.Failed).ToList();

        var sb = new StringBuilder();
        if (succeeded.Count > 0) sb.AppendLine($"Installed/updated: {succeeded.Count}");
        if (failed.Count    > 0) sb.AppendLine($"Failed:            {failed.Count}");

        if (failed.Count > 0)
        {
            sb.AppendLine("\nDetails:");
            foreach (var r in failed)
                sb.AppendLine($"  • {r.App.Name}: {r.ErrorMessage ?? "Unknown error"}");
        }

        var icon = failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        MessageBox.Show(sb.ToString().Trim(), "Install Complete", MessageBoxButton.OK, icon);
    }
}

using ManagedDrive.App.Localization;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Initializes the main window and binds the supplied view model.
    /// </summary>
    /// <param name="viewModel">The view model to bind as <c>DataContext</c>.</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        UpdateColumnHeaders();
        LanguageManager.Instance.LanguageChanged += (_, _) => UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        ColMountPoint.Header = Loc.Get("Col.MountPoint");
        ColLabel.Header      = Loc.Get("Col.Label");
        ColCapacity.Header   = Loc.Get("Col.Capacity");
        ColUsed.Header       = Loc.Get("Col.Used");
        ColFree.Header       = Loc.Get("Col.Free");
        ColUsage.Header      = Loc.Get("Col.Usage");
    }
}

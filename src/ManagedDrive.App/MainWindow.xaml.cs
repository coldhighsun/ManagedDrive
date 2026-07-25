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
        WindowMaximizeHelper.HookMaximizeBehavior(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ImportBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    private void OverflowBtn_Click(object sender, RoutedEventArgs e) => OpenAttachedContextMenu(sender);

    private void OpenAttachedContextMenu(object sender)
    {
        if (sender is FrameworkElement { ContextMenu: not null } btn)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
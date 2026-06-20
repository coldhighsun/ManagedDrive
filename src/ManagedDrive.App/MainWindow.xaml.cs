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
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing when the user clicks X.
        e.Cancel = true;
        Hide();
    }
}
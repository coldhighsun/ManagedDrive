namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="ConfirmDialog"/>.
/// </summary>
public partial class ConfirmDialog
{
    /// <summary>
    /// Initializes the dialog with the given title and body text.
    /// </summary>
    /// <param name="title">Header text shown next to the warning icon.</param>
    /// <param name="body">Descriptive message shown below the header.</param>
    public ConfirmDialog(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
    }

    public bool IsOptionChecked => OptionCheckBox.IsChecked == true;

    public void ShowOption(string label)
    {
        OptionCheckBox.Content = label;
        OptionCheckBox.Visibility = Visibility.Visible;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
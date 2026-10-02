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
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        Loaded += (_, _) => DialogPlacement.BringToFrontWhenUnowned(this);
    }

    /// <summary>
    /// Gets whether the optional checkbox is checked.
    /// </summary>
    public bool IsOptionChecked => OptionCheckBox.IsChecked == true;

    /// <summary>
    /// Shows the optional checkbox with the given label.
    /// </summary>
    public void ShowOption(string label)
    {
        OptionCheckBox.Content = label;
        OptionCheckBox.Visibility = Visibility.Visible;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}

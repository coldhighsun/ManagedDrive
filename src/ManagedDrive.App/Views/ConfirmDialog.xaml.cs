namespace ManagedDrive.App.Views;

/// <summary>
/// Interaction logic for <see cref="ConfirmDialog"/>.
/// </summary>
public partial class ConfirmDialog
{
    private ConfirmKind _kind;

    /// <summary>
    /// Initializes the dialog with the given title and body text.
    /// </summary>
    /// <param name="title">Header text shown next to the icon.</param>
    /// <param name="body">Descriptive message shown below the header.</param>
    public ConfirmDialog(string title, string body)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        Kind = ConfirmKind.Warning;
        BodyText.Text = body;
        Loaded += (_, _) => DialogPlacement.BringToFrontWhenUnowned(this);
    }

    /// <summary>
    /// Gets or sets what the dialog is about; the icon and its color follow. Warning unless said otherwise.
    /// </summary>
    public ConfirmKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            var (glyph, brushKey) = ConfirmKindIcons.Get(value);
            KindIcon.Text = glyph;
            KindIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
        }
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

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
    /// Gets or sets whether the Cancel button is shown. Off turns the dialog into a plain notice: only OK, which
    /// Esc and the close box also take.
    /// </summary>
    public bool ShowCancel
    {
        get => CancelButton.Visibility == Visibility.Visible;
        set
        {
            CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateEscapeButton();
        }
    }

    /// <summary>
    /// Gets or sets the text of the OK button (for example "Yes"); the default label is kept when unset.
    /// </summary>
    public string? OkText
    {
        get => OkButton.Content as string;
        set { if (value is not null) { OkButton.Content = value; } }
    }

    /// <summary>
    /// Gets or sets the text of the Cancel button (for example "No"); the default label is kept when unset.
    /// </summary>
    public string? CancelText
    {
        get => CancelButton.Content as string;
        set { if (value is not null) { CancelButton.Content = value; } }
    }

    /// <summary>
    /// Gets or sets the text of an optional third button between Cancel and OK (for example "No" next to Yes and
    /// Cancel); <c>null</c> (the default) hides it. Pressing it closes the dialog with the result <c>false</c>, as
    /// for Cancel, and sets <see cref="IsNeutralChosen"/>.
    /// </summary>
    public string? NeutralText
    {
        get => NeutralButton.Visibility == Visibility.Visible ? NeutralButton.Content as string : null;
        set
        {
            NeutralButton.Content = value;
            NeutralButton.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            ArrangeButtons(value is not null);
        }
    }

    /// <summary>
    /// Gets whether the middle button (see <see cref="NeutralText"/>) was the one pressed.
    /// </summary>
    public bool IsNeutralChosen { get; private set; }

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

    // Three buttons read Yes, No, Cancel (the Windows order); two keep Cancel, OK with OK as the primary on the right.
    private void ArrangeButtons(bool threeButtons)
    {
        var gap = new Thickness(0, 0, 8, 0);
        ButtonPanel.Children.Clear();
        if (threeButtons)
        {
            OkButton.Margin = gap;
            NeutralButton.Margin = gap;
            CancelButton.Margin = default;
            ButtonPanel.Children.Add(OkButton);
            ButtonPanel.Children.Add(NeutralButton);
            ButtonPanel.Children.Add(CancelButton);
        }
        else
        {
            OkButton.Margin = default;
            NeutralButton.Margin = gap;
            CancelButton.Margin = gap;
            ButtonPanel.Children.Add(CancelButton);
            ButtonPanel.Children.Add(NeutralButton);
            ButtonPanel.Children.Add(OkButton);
        }
    }

    // Esc takes Cancel when it is shown, otherwise OK, so a notice still closes on Esc.
    private void UpdateEscapeButton()
    {
        CancelButton.IsCancel = ShowCancel;
        OkButton.IsCancel = !ShowCancel;
    }

    private void NeutralButton_Click(object sender, RoutedEventArgs e)
    {
        IsNeutralChosen = true;
        DialogResult = false;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}

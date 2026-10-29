namespace ManagedDrive.App.Controls;

/// <summary>
/// Slider for a compression level shown as a plain number: 0 means "no compression", higher
/// numbers are Zstd levels (or the 1-9 scale of zip/7z archives when <see cref="Maximum"/> is 9).
/// </summary>
public partial class CompressionLevelPicker
{
    /// <summary>
    /// Identifies the <see cref="Level"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(int), typeof(CompressionLevelPicker),
        new(ZstdLevels.Default, OnLevelChanged, CoerceLevel));

    /// <summary>
    /// Identifies the <see cref="Maximum"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(CompressionLevelPicker),
        new(ZstdLevels.Max, OnMaximumChanged));

    /// <summary>
    /// Initializes the control.
    /// </summary>
    public CompressionLevelPicker()
    {
        InitializeComponent();
        _ready = true;
        LevelSlider.Value = Level;
        UpdateText();

        // The "off" caption is looked up by language, so refresh it whenever the control is shown.
        Loaded += (_, _) => UpdateText();
    }

    /// <summary>
    /// Set once <c>InitializeComponent</c> is done; slider events raised while XAML is still
    /// being applied must not touch <see cref="Level"/> or the not yet created caption.
    /// </summary>
    private readonly bool _ready;

    /// <summary>
    /// Gets or sets the selected level (0 = none, 1 to <see cref="Maximum"/>).
    /// </summary>
    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>
    /// Gets or sets the highest selectable level; the current level is clamped to it.
    /// </summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Text shown for a level: its number, or the "no compression" caption for 0.
    /// </summary>
    /// <param name="level">The level to describe.</param>
    /// <returns>The display text.</returns>
    internal static string FormatLevel(int level) =>
        level == ZstdLevels.None ? Loc.Get("CreateDisk.CompressionNone") : level.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// Keeps <see cref="Level"/> between 0 and <see cref="Maximum"/>.
    /// </summary>
    private static object CoerceLevel(DependencyObject d, object baseValue)
    {
        var picker = (CompressionLevelPicker)d;
        return Math.Clamp((int)baseValue, ZstdLevels.None, picker.Maximum);
    }

    /// <summary>
    /// Pushes a new <see cref="Level"/> into the slider and the caption.
    /// </summary>
    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (CompressionLevelPicker)d;
        if (!picker._ready)
        {
            return;
        }

        picker.LevelSlider.Value = (int)e.NewValue;
        picker.UpdateText();
    }

    /// <summary>
    /// Applies a new <see cref="Maximum"/> to the slider and re-clamps the level.
    /// </summary>
    private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (CompressionLevelPicker)d;
        if (!picker._ready)
        {
            return;
        }

        picker.LevelSlider.Maximum = (int)e.NewValue;
        picker.CoerceValue(LevelProperty);
    }

    /// <summary>
    /// Copies the slider position into <see cref="Level"/>.
    /// </summary>
    private void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
        {
            return;
        }

        Level = (int)Math.Round(e.NewValue);
    }

    /// <summary>
    /// Refreshes the caption next to the slider.
    /// </summary>
    private void UpdateText()
    {
        LevelText.Text = FormatLevel(Level);
    }
}

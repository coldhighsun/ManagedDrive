using ManagedDrive.Cli.Core;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace ManagedDrive.App.Controls;

/// <summary>
/// Draws the contents of a directory as a squarified treemap: every file is a rectangle whose
/// area is the memory it occupies, coloured by kind, and directories frame their children.
/// Hover shows details, a double click opens a directory, and the keyboard works too (arrow keys
/// move, Enter opens, Backspace goes up). Rendering is done directly into a
/// <see cref="DrawingContext"/> so thousands of cells cost no UI elements.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    /// <summary>
    /// Tuning shared by layout and drawing, so the header height drawn is the one reserved.
    /// </summary>
    private static readonly TreemapOptions Options = new();

    /// <summary>
    /// The root of the whole tree, whose size is the base of the "share of the disk" figures.
    /// </summary>
    private SpaceNode? _root;

    /// <summary>
    /// The directory whose contents are shown.
    /// </summary>
    private SpaceNode? _directory;

    /// <summary>
    /// The cells of the current layout, parents before their children.
    /// </summary>
    private IReadOnlyList<TreemapCell> _cells = [];

    /// <summary>
    /// Index in <see cref="_cells"/> of the cell under the mouse, or -1.
    /// </summary>
    private int _hover = -1;

    /// <summary>
    /// The selected file or directory; kept as a node so it survives a re-layout.
    /// </summary>
    private SpaceNode? _selected;

    /// <summary>
    /// The tooltip of the hovered cell. It is opened by hand because the control's own
    /// <c>ToolTip</c> property is only armed on mouse enter, which is too early to know the cell.
    /// </summary>
    private readonly System.Windows.Controls.ToolTip _tip = new()
    {
        Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// Creates the control.
    /// </summary>
    public TreemapControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        Loaded += (_, _) => ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>
    /// Raised after the shown directory changed.
    /// </summary>
    public event EventHandler? DirectoryChanged;

    /// <summary>
    /// Raised after the selection changed.
    /// </summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised when the user asks, from the context menu, to show a node in Windows Explorer. The
    /// control does not know where the disk is mounted, so the host does the opening.
    /// </summary>
    public event EventHandler<SpaceNode>? OpenInExplorerRequested;

    /// <summary>
    /// Raised when the user asks, from the context menu, to delete a node. The host confirms and
    /// does the deleting.
    /// </summary>
    public event EventHandler<SpaceNode>? DeleteRequested;

    /// <summary>
    /// Whether the context menu offers deleting; <c>false</c> (for example on a read-only disk)
    /// shows the entry disabled.
    /// </summary>
    public bool CanDelete { get; set; } = true;

    /// <summary>
    /// The directory shown. Setting it re-lays the map out and clears the selection.
    /// </summary>
    public SpaceNode? Directory
    {
        get => _directory;
        set
        {
            if (ReferenceEquals(_directory, value))
            {
                return;
            }

            _directory = value;
            SetSelection(null);
            Relayout();
            DirectoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The selected file or directory, or <c>null</c>.
    /// </summary>
    public SpaceNode? SelectedNode => _selected;

    /// <summary>
    /// Whether there is a parent directory to go up to.
    /// </summary>
    public bool CanGoUp => _directory?.Parent is not null;

    /// <summary>
    /// Shows a new tree, starting at its root.
    /// </summary>
    /// <param name="root">The tree, or <c>null</c> to clear the map.</param>
    public void SetTree(SpaceNode? root)
    {
        _root = root;
        _directory = null;
        Directory = root;
        if (root is null)
        {
            Relayout();
        }
    }

    /// <summary>
    /// Goes to the parent of the shown directory, selecting the directory just left.
    /// </summary>
    public void GoUp()
    {
        if (_directory?.Parent is not { } parent)
        {
            return;
        }

        var left = _directory;
        Directory = parent;
        SetSelection(left);
    }

    /// <summary>
    /// Makes <paramref name="node"/> the shown directory.
    /// </summary>
    /// <param name="node">A directory in the tree.</param>
    public void Open(SpaceNode node)
    {
        if (node.IsDirectory)
        {
            Directory = node;
        }
    }

    /// <summary>
    /// Selects a node of the shown directory (or any node below it that is drawn).
    /// </summary>
    /// <param name="node">The node, or <c>null</c> to clear the selection.</param>
    public void Select(SpaceNode? node) => SetSelection(node);

    /// <summary>
    /// Describes a node for tooltips and the status line.
    /// </summary>
    /// <param name="node">The file or directory.</param>
    /// <returns>Path, size and share of the disk, and for a directory its file count.</returns>
    public string Describe(SpaceNode node)
    {
        var size = ByteFormatter.Format(node.Allocated);
        var share = Percent(node.Allocated, _root?.Allocated ?? 0);
        return node.IsDirectory
            ? Loc.Format("SpaceUsage.TipFolder", node.Path, size, share, node.FileCount)
            : Loc.Format("SpaceUsage.TipFile", node.Path, size, share);
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Relayout();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var palette = new Palette(this);
        drawingContext.DrawRectangle(palette.Surface, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var typeface = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i < _cells.Count; i++)
        {
            DrawCell(drawingContext, _cells[i], palette, typeface, dpi, _root?.Allocated ?? 0);
        }

        if (_hover >= 0 && _hover < _cells.Count)
        {
            drawingContext.DrawRectangle(palette.Hover, null, ToRect(_cells[_hover].Rect));
        }

        if (_selected is not null && FindCell(_selected) is { } selected)
        {
            var rect = ToRect(selected.Rect);
            rect.Inflate(-1, -1);
            drawingContext.DrawRectangle(null, new Pen(palette.Foreground, 2), rect);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        _tip.HorizontalOffset = position.X + 14;
        _tip.VerticalOffset = position.Y + 18;

        var index = HitTest(position);
        if (index == _hover)
        {
            return;
        }

        _hover = index;
        if (index >= 0)
        {
            _tip.Content = TipFor(_cells[index]);
            _tip.PlacementTarget = this;
            _tip.IsOpen = true;
        }
        else
        {
            _tip.IsOpen = false;
        }

        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var index = HitTest(e.GetPosition(this));
        var node = index >= 0 ? _cells[index].Node : null;
        SetSelection(node);

        if (e.ClickCount == 2 && node is { IsDirectory: true })
        {
            Directory = node;
        }
    }

    /// <inheritdoc />
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        var index = HitTest(e.GetPosition(this));
        var node = index >= 0 ? _cells[index].Node : null;
        SetSelection(node);

        var menu = new ContextMenu { PlacementTarget = this };
        if (node is not null)
        {
            var open = new MenuItem { Header = Loc.Get(node.IsDirectory ? "SpaceUsage.OpenFolder" : "SpaceUsage.OpenContainingFolder") };
            open.Click += (_, _) => OpenInExplorerRequested?.Invoke(this, node);
            menu.Items.Add(open);

            var delete = new MenuItem
            {
                Header = Loc.Get(node.IsDirectory ? "SpaceUsage.DeleteFolder" : "SpaceUsage.DeleteFile"),
                IsEnabled = CanDelete,
            };
            delete.Click += (_, _) => DeleteRequested?.Invoke(this, node);
            menu.Items.Add(delete);

            var copy = new MenuItem { Header = Loc.Get("SpaceUsage.CopyPath") };
            copy.Click += (_, _) => TryCopy(node.Path);
            menu.Items.Add(copy);
        }

        if (CanGoUp)
        {
            var up = new MenuItem { Header = Loc.Get("SpaceUsage.Up") };
            up.Click += (_, _) => GoUp();
            menu.Items.Add(up);
        }

        if (menu.Items.Count > 0)
        {
            menu.IsOpen = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Enter when _selected is { IsDirectory: true } directory:
                Directory = directory;
                break;
            case Key.Back:
                GoUp();
                break;
            case Key.Left:
                MoveSelection(-1, 0);
                break;
            case Key.Right:
                MoveSelection(1, 0);
                break;
            case Key.Up:
                MoveSelection(0, -1);
                break;
            case Key.Down:
                MoveSelection(0, 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Redraws after a theme switch so the category colours follow it.
    /// </summary>
    /// <param name="sender">The theme manager.</param>
    /// <param name="e">Unused.</param>
    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>
    /// Recomputes the cells for the current directory and size, and redraws.
    /// </summary>
    private void Relayout()
    {
        _hover = -1;
        _tip.IsOpen = false;
        _cells = _directory is not null && ActualWidth > 0 && ActualHeight > 0
            ? TreemapBuilder.Build(_directory, new(0, 0, ActualWidth, ActualHeight), Options)
            : [];
        InvalidateVisual();
    }

    /// <summary>
    /// Changes the selection and tells listeners and screen readers.
    /// </summary>
    /// <param name="node">The newly selected node, or <c>null</c>.</param>
    private void SetSelection(SpaceNode? node)
    {
        if (ReferenceEquals(_selected, node))
        {
            return;
        }

        _selected = node;
        AutomationProperties.SetName(this, node is null ? string.Empty : Describe(node).Replace('\n', ' '));
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the selection to the nearest cell in a direction.
    /// </summary>
    /// <param name="dx">-1 for left, 1 for right, otherwise 0.</param>
    /// <param name="dy">-1 for up, 1 for down, otherwise 0.</param>
    private void MoveSelection(int dx, int dy)
    {
        var candidates = _cells.Where(cell => cell.Node is not null && !cell.IsContainer).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        if (_selected is null || FindCell(_selected) is not { } from)
        {
            SetSelection(candidates[0].Node);
            return;
        }

        var origin = Center(from.Rect);
        TreemapCell? best = null;
        var bestScore = double.MaxValue;
        foreach (var cell in candidates)
        {
            if (ReferenceEquals(cell.Node, _selected))
            {
                continue;
            }

            var center = Center(cell.Rect);
            var along = ((center.X - origin.X) * dx) + ((center.Y - origin.Y) * dy);
            if (along <= 0)
            {
                continue;
            }

            var across = Math.Abs(((center.X - origin.X) * dy) + ((center.Y - origin.Y) * dx));
            var score = along + (2 * across);
            if (score < bestScore)
            {
                bestScore = score;
                best = cell;
            }
        }

        if (best is not null)
        {
            SetSelection(best.Node);
        }
    }

    /// <summary>
    /// Finds the innermost cell containing a point.
    /// </summary>
    /// <param name="point">A point in the control.</param>
    /// <returns>The index into the cells, or -1.</returns>
    private int HitTest(Point point)
    {
        for (var i = _cells.Count - 1; i >= 0; i--)
        {
            if (_cells[i].Rect.Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the cell drawn for a node.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The cell, or <c>null</c> if the node is not drawn (it may be merged or deeper than shown).</returns>
    private TreemapCell? FindCell(SpaceNode node) => _cells.FirstOrDefault(cell => ReferenceEquals(cell.Node, node));

    /// <summary>
    /// Builds the tooltip of a cell.
    /// </summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The tooltip text.</returns>
    private string TipFor(TreemapCell cell) => cell.Node is { } node
        ? Describe(node)
        : Loc.Format("SpaceUsage.TipMerged", cell.MergedCount, ByteFormatter.Format(cell.MergedBytes), Percent(cell.MergedBytes, _root?.Allocated ?? 0));

    /// <summary>
    /// Draws one cell: fill, frame and, where there is room, its label.
    /// </summary>
    /// <param name="dc">The drawing context.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="palette">The theme colours.</param>
    /// <param name="typeface">The text face.</param>
    /// <param name="dpi">Pixels per device independent pixel, for text.</param>
    /// <param name="total">Memory of the whole tree, the base of the share figures.</param>
    private static void DrawCell(DrawingContext dc, TreemapCell cell, Palette palette, Typeface typeface, double dpi, ulong total)
    {
        var rect = ToRect(cell.Rect);
        if (rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        if (cell.Node is null)
        {
            dc.DrawRectangle(palette.ContainerHeader, palette.SurfacePen, rect);
            DrawLabel(dc, Loc.Format("SpaceUsage.MergedLabel", cell.MergedCount), rect, palette.Foreground, typeface, dpi, 11);
            if (rect.Height >= 34)
            {
                DrawLabel(dc, ByteFormatter.Format(cell.MergedBytes), new(rect.X, rect.Y + 15, rect.Width, rect.Height - 15), palette.Foreground, typeface, dpi, 10, 0.8);
            }

            return;
        }

        var node = cell.Node;
        if (cell.IsContainer)
        {
            dc.DrawRectangle(palette.Container, palette.DividerPen, rect);
            if (cell.HasHeader)
            {
                var header = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Options.HeaderHeight);
                dc.DrawRectangle(palette.ContainerHeader, null, header);
                var summary = $"{node.Name}  {ByteFormatter.Format(node.Allocated)}";
                if (header.Width >= 200)
                {
                    summary += $"  ·  {Percent(node.Allocated, total)}";
                }

                DrawLabel(dc, summary, header, palette.Foreground, typeface, dpi, 11);
            }

            return;
        }

        var fill = node.IsDirectory ? palette.Folder : palette.ForCategory(FileCategories.Of(node.Name));
        dc.DrawRectangle(fill, palette.SurfacePen, rect);

        var inner = rect;
        inner.Inflate(-1, -1);
        DrawLabel(dc, node.Name, inner, Brushes.White, typeface, dpi, 11);
        if (inner.Height >= 34)
        {
            var size = ByteFormatter.Format(node.Allocated);
            var detail = inner.Width >= 110 ? $"{size}  ·  {Percent(node.Allocated, total)}" : size;
            DrawLabel(dc, detail, new(inner.X, inner.Y + 15, inner.Width, inner.Height - 15), Brushes.White, typeface, dpi, 10, 0.85);
        }

        if (node.IsDirectory && inner.Height >= 50)
        {
            DrawLabel(dc, Loc.Format("SpaceUsage.CellFiles", node.FileCount), new(inner.X, inner.Y + 29, inner.Width, inner.Height - 29), Brushes.White, typeface, dpi, 10, 0.7);
        }
    }

    /// <summary>
    /// Draws a single line of text clipped to a rectangle, if the rectangle is big enough to read.
    /// </summary>
    /// <param name="dc">The drawing context.</param>
    /// <param name="text">The text.</param>
    /// <param name="rect">Where it goes.</param>
    /// <param name="brush">The text colour.</param>
    /// <param name="typeface">The text face.</param>
    /// <param name="dpi">Pixels per device independent pixel.</param>
    /// <param name="size">Font size.</param>
    /// <param name="opacity">Opacity of the text.</param>
    private static void DrawLabel(
        DrawingContext dc, string text, Rect rect, Brush brush, Typeface typeface, double dpi, double size, double opacity = 1)
    {
        if (rect.Width < 36 || rect.Height < size + 3)
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, dpi)
        {
            MaxTextWidth = rect.Width - 6,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (opacity < 1)
        {
            dc.PushOpacity(opacity);
        }

        dc.DrawText(formatted, new(rect.X + 3, rect.Y + 1));
        if (opacity < 1)
        {
            dc.Pop();
        }
    }

    /// <summary>
    /// Converts a layout rectangle to a WPF one.
    /// </summary>
    /// <param name="rect">The layout rectangle.</param>
    /// <returns>The same area as a <see cref="Rect"/>.</returns>
    private static Rect ToRect(TreemapRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>
    /// The centre of a layout rectangle.
    /// </summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns>Its centre.</returns>
    private static Point Center(TreemapRect rect) => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

    /// <summary>
    /// Formats a share as a percentage.
    /// </summary>
    /// <param name="part">The part, in bytes.</param>
    /// <param name="whole">The whole, in bytes.</param>
    /// <returns>For example <c>12.3%</c>; <c>0%</c> when the whole is zero.</returns>
    private static string Percent(ulong part, ulong whole) =>
        whole == 0 ? "0%" : $"{(double)part / whole * 100.0:0.#}%";

    /// <summary>
    /// Puts text on the clipboard, ignoring the clipboard being locked by another program.
    /// </summary>
    /// <param name="text">The text to copy.</param>
    private static void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; copying is a convenience, not worth an error.
        }
    }

    /// <summary>
    /// The theme's brushes and pens, looked up once per frame so a theme switch is picked up.
    /// </summary>
    private sealed class Palette
    {
        /// <summary>
        /// The control the resources are looked up from.
        /// </summary>
        private readonly FrameworkElement _owner;

        /// <summary>
        /// Looks the brushes up for one frame.
        /// </summary>
        /// <param name="owner">The control being drawn.</param>
        public Palette(FrameworkElement owner)
        {
            _owner = owner;
            Surface = Find("AppSurface");
            Foreground = Find("AppForeground");
            Container = Find("AppSpaceContainer");
            ContainerHeader = Find("AppSpaceContainerHeader");
            Folder = Find("AppSpaceFolder");
            Hover = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            SurfacePen = new(Surface, 1);
            DividerPen = new(Find("AppDivider"), 1);
        }

        /// <summary>
        /// The window surface, also the gap between cells.
        /// </summary>
        public Brush Surface { get; }

        /// <summary>
        /// Normal text.
        /// </summary>
        public Brush Foreground { get; }

        /// <summary>
        /// Body of a directory that shows its children.
        /// </summary>
        public Brush Container { get; }

        /// <summary>
        /// Title bar of such a directory.
        /// </summary>
        public Brush ContainerHeader { get; }

        /// <summary>
        /// A directory too small to open.
        /// </summary>
        public Brush Folder { get; }

        /// <summary>
        /// Light veil over the hovered cell.
        /// </summary>
        public Brush Hover { get; }

        /// <summary>
        /// Thin frame in the surface colour.
        /// </summary>
        public Pen SurfacePen { get; }

        /// <summary>
        /// Thin frame in the divider colour.
        /// </summary>
        public Pen DividerPen { get; }

        /// <summary>
        /// The colour of a file category.
        /// </summary>
        /// <param name="category">The category.</param>
        /// <returns>The brush from the theme.</returns>
        public Brush ForCategory(FileCategory category) => Find($"AppSpace{category}");

        /// <summary>
        /// Looks a brush up in the current theme.
        /// </summary>
        /// <param name="key">The resource key.</param>
        /// <returns>The brush, grey if the theme lacks it.</returns>
        private Brush Find(string key) => _owner.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

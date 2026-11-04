using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiffRapid.Core.Imaging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using Rectangle = System.Windows.Shapes.Rectangle;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Image comparison. Both files are decoded with WPF (any format WIC reads), compared pixel by pixel in the
/// background, and shown side by side with the difference regions outlined, or as one combined picture:
/// highlight, XOR, blend, swipe or subtract. Zoom and scrolling are shared by every view.
/// </summary>
internal sealed class ImageDiffControl : DockPanel, IComparisonView, IOleCommandTarget
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64;
    private const double ZoomStep = 1.25;
    private const string HintText = "Drag to pan  ·  Ctrl+wheel or +/- zooms  ·  F7 / Shift+F7 steps through differences  ·  F5 compares again";

    private static readonly TimeSpan ToleranceDelay = TimeSpan.FromMilliseconds(250);

    // Grey squares at partial opacity over the window background: a checkerboard that suits light and dark themes.
    private static readonly Brush Checkerboard = CreateCheckerboard();

    private readonly DiffRapidPackage _package;
    private readonly string _leftPath;
    private readonly string _rightPath;
    private readonly ImagePane _primary = new();
    private readonly ImagePane _secondary = new();
    private readonly Grid _body = new();
    private readonly Border _paneDivider = new() { Width = 1 };
    private readonly Dictionary<ImageViewMode, ToggleButton> _modeToggles = new();
    private readonly Dictionary<ImageViewMode, BitmapSource> _overlays = new();
    private readonly HashSet<ImageViewMode> _rendering = new();
    private readonly DispatcherTimer _toleranceTimer;
    private readonly Slider _toleranceSlider = CreateSlider(0, 255, 0, "Colour tolerance: how far a channel may differ (0 to 255) and still count as the same");
    private readonly Slider _blendSlider = CreateSlider(0, 100, 50, "How much of the right image is blended over the left one");
    private readonly Slider _swipeSlider = CreateSlider(0, 100, 50, "Where the left image ends and the right image begins");
    private readonly TextBlock _toleranceValue = new() { MinWidth = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBlock _zoomValue = new() { MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _leftSize = SizeLabel();
    private readonly TextBlock _rightSize = SizeLabel();
    private FrameworkElement? _blendGroup;
    private FrameworkElement? _swipeGroup;
    private ImageViewMode _mode = ImageViewMode.SideBySide;
    private LoadedImages? _loaded;
    private ImageDiffResult? _result;
    private CancellationTokenSource? _compareCancellation;
    private ImagePane _leader;
    private int _currentRegion = -1;
    private double _zoom = 1;
    private double _dpiScale = 1;
    private bool _fitToWindow = true;
    private bool _updatingModes;
    private bool _disposed;

    public ImageDiffControl(DiffRapidPackage package, string leftPath, string rightPath)
    {
        _package = package;
        _leftPath = Path.GetFullPath(leftPath);
        _rightPath = Path.GetFullPath(rightPath);
        _leader = _primary;

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Focusable = true;
        FocusVisualStyle = null;

        _toleranceTimer = new DispatcherTimer(ToleranceDelay, DispatcherPriority.Background, (_, _) => OnToleranceSettled(), Dispatcher);
        _toleranceTimer.Stop();

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var header = BuildHeader();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildBody());

        WireSliders();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => OnLoaded();
        UpdateZoomText();
        StartCompare(reload: true);
    }

    /// <summary>The ways the two images can be shown.</summary>
    private enum ImageViewMode
    {
        SideBySide,
        Highlight,
        Xor,
        Blend,
        Swipe,
        Subtract,
    }

    public bool IsDirty => false;

    /// <summary>Scale applied to the image surfaces, in device-independent units per image pixel.</summary>
    private double SurfaceScale => _zoom / _dpiScale;

    private IEnumerable<ImagePane> Panes
    {
        get
        {
            yield return _primary;
            yield return _secondary;
        }
    }

    public void Save()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toleranceTimer.Stop();
        _compareCancellation?.Cancel();
    }

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };

        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.GoToPrevious), "Previous difference region (Shift+F7)", () => GoToRegion(-1)));
        commands.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.GoToNext), "Next difference region (F7)", () => GoToRegion(+1)));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(DiffTheme.Caption("VIEW"));
        commands.Children.Add(ModeToggle(ImageViewMode.SideBySide, "Side by side", "Both images next to each other, with the difference regions outlined"));
        commands.Children.Add(ModeToggle(ImageViewMode.Highlight, "Highlight", "A faded copy of the image with every differing pixel in red"));
        commands.Children.Add(ModeToggle(ImageViewMode.Xor, "XOR", "Exclusive-or of the colours: matching pixels turn black"));
        commands.Children.Add(ModeToggle(ImageViewMode.Blend, "Blend", "The right image over the left one, at the opacity set by the slider"));
        commands.Children.Add(ModeToggle(ImageViewMode.Swipe, "Swipe", "The left image up to the slider, the right image beyond it"));
        commands.Children.Add(ModeToggle(ImageViewMode.Subtract, "Subtract", "The difference of each colour channel, amplified so small changes show"));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(_verdict);
        SetDock(commands, Dock.Left);
        bar.Children.Add(commands);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        _blendGroup = SliderGroup("BLEND", _blendSlider, null);
        _swipeGroup = SliderGroup("SWIPE", _swipeSlider, null);
        settings.Children.Add(_blendGroup);
        settings.Children.Add(_swipeGroup);
        settings.Children.Add(SliderGroup("TOLERANCE", _toleranceSlider, _toleranceValue));
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.ZoomOut), "Zoom out (-)", () => ZoomBy(1 / ZoomStep)));
        settings.Children.Add(_zoomValue);
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.ZoomIn), "Zoom in (+)", () => ZoomBy(ZoomStep)));
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.ZoomToFit), "Fit the images to the window", FitToWindow));
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.ZoomOriginalSize), "Actual size: one image pixel per screen pixel (100%)", () => SetZoomAtCentre(1)));
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Load the files and compare again (F5)", () => StartCompare(reload: true)));
        SetDock(settings, Dock.Right);
        bar.Children.Add(settings);

        var chrome = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        chrome.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.CommandBarGradientBeginColorKey);
        return chrome;
    }

    private ToggleButton ModeToggle(ImageViewMode mode, string text, string tooltip)
    {
        var toggle = DiffTheme.OptionToggle(text, tooltip, isOn => OnModeToggled(mode, isOn), isChecked: mode == _mode);
        _modeToggles[mode] = toggle;
        return toggle;
    }

    private static FrameworkElement SliderGroup(string caption, Slider slider, TextBlock? value)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
        group.Children.Add(DiffTheme.Caption(caption));
        group.Children.Add(slider);
        if (value is not null)
        {
            group.Children.Add(value);
        }

        return group;
    }

    private static Slider CreateSlider(double minimum, double maximum, double value, string tooltip) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Width = 100,
        SmallChange = 1,
        LargeChange = 10,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tooltip,
        Focusable = false,
    };

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = SideLabel("LEFT", _leftPath, DiffTheme.RemovedBrush, _leftSize);
        var right = SideLabel("RIGHT", _rightPath, DiffTheme.AddedBrush, _rightSize);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var chrome = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 7, 10, 7) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    private static FrameworkElement SideLabel(string side, string path, Brush accent, TextBlock size)
    {
        var badge = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = side, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        };
        var text = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel { ToolTip = path, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock SizeLabel()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return label;
    }

    private FrameworkElement BuildFooter()
    {
        var panel = new DockPanel { Margin = new Thickness(10, 3, 10, 3) };
        SetDock(_summary, Dock.Left);
        panel.Children.Add(_summary);
        panel.Children.Add(_details);
        _details.Text = HintText;

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return chrome;
    }

    // Two panes split down the middle; the right one (and the divider) is hidden in the single-picture views.
    private UIElement BuildBody()
    {
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _paneDivider.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        Grid.SetColumn(_paneDivider, 1);
        Grid.SetColumn(_secondary.Scroller, 2);
        _body.Children.Add(_primary.Scroller);
        _body.Children.Add(_paneDivider);
        _body.Children.Add(_secondary.Scroller);

        foreach (var pane in Panes)
        {
            WirePane(pane);
        }

        _primary.Scroller.SizeChanged += (_, _) =>
        {
            if (_fitToWindow)
            {
                ApplyFit();
            }
        };
        return _body;
    }

    private void WirePane(ImagePane pane)
    {
        pane.Backdrop.Background = Checkerboard;
        pane.Scroller.MouseEnter += (_, _) => _leader = pane;
        pane.Scroller.ScrollChanged += (_, _) => MirrorScroll(pane);
        pane.Scroller.PreviewMouseWheel += (_, e) => OnMouseWheel(pane, e);
        pane.Backdrop.MouseLeftButtonDown += (_, e) => BeginPan(pane, e);
        pane.Backdrop.MouseMove += (_, e) => OnPaneMouseMove(pane, e);
        pane.Backdrop.MouseLeftButtonUp += (_, _) => EndPan(pane);
        pane.Backdrop.LostMouseCapture += (_, _) => pane.PanStart = null;
        pane.Backdrop.MouseLeave += (_, _) => _details.Text = HintText;
    }

    private void WireSliders()
    {
        _toleranceValue.Text = "0";
        _toleranceSlider.ValueChanged += (_, _) =>
        {
            _toleranceValue.Text = ((int)_toleranceSlider.Value).ToString(CultureInfo.CurrentCulture);
            _toleranceTimer.Stop();
            _toleranceTimer.Start();
        };
        _blendSlider.ValueChanged += (_, _) => UpdateBlend();
        _swipeSlider.ValueChanged += (_, _) => UpdateSwipe();
    }

    private void OnLoaded()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi > 0)
        {
            _dpiScale = dpi;
        }

        if (_fitToWindow)
        {
            ApplyFit();
        }
        else
        {
            ApplyZoom();
        }

        Focus();
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    // ---- Loading and comparing ----

    private void OnToleranceSettled()
    {
        _toleranceTimer.Stop();
        StartCompare(reload: false);
    }

    /// <summary>
    /// Compares in the background. With <paramref name="reload"/> the files are decoded again first; otherwise
    /// the images already loaded are compared with the current tolerance.
    /// </summary>
    private void StartCompare(bool reload)
    {
        if (_disposed)
        {
            return;
        }

        _compareCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _compareCancellation = cancellation;
        var loaded = reload ? null : _loaded;
        var options = new ImageCompareOptions { ColorTolerance = (int)_toleranceSlider.Value };
        _verdict.Text = loaded is null ? "Loading…" : "Comparing…";
        _verdict.ClearValue(TextBlock.ForegroundProperty);

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var (images, result) = await Task.Run(
                    () =>
                    {
                        var decoded = loaded ?? LoadImages(_leftPath, _rightPath, cancellation.Token);
                        return (decoded, ImageComparer.Compare(decoded.Left, decoded.Right, options, cancellation.Token));
                    },
                    cancellation.Token);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    Complete(images, result);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    ShowFailure(ex.Message);
                }
            }
        }).FileAndForget("DiffRapid/ImageCompare");
    }

    private void Complete(LoadedImages images, ImageDiffResult result)
    {
        _loaded = images;
        _result = result;
        _overlays.Clear();
        _rendering.Clear();
        _currentRegion = -1;
        _leftSize.Text = SizeText(result.LeftWidth, result.LeftHeight);
        _rightSize.Text = SizeText(result.RightWidth, result.RightHeight);
        foreach (var pane in Panes)
        {
            pane.Surface.Width = result.Width;
            pane.Surface.Height = result.Height;
        }

        UpdateSummary();
        ShowMode();
    }

    private void ShowFailure(string message)
    {
        _verdict.Text = "Compare failed";
        _verdict.Foreground = DiffTheme.RemovedBrush;
        _summary.Text = message;
    }

    private void UpdateSummary()
    {
        if (_result is not { } result)
        {
            return;
        }

        var sizes = $"{SizeText(result.LeftWidth, result.LeftHeight)} vs {SizeText(result.RightWidth, result.RightHeight)}";
        if (result.AreIdentical)
        {
            _verdict.Text = "✔  Images are identical";
            _verdict.Foreground = DiffTheme.AddedBrush;
            _summary.Text = $"No pixels differ; {sizes}";
            return;
        }

        var regions = result.DifferenceRegions.Count;
        _verdict.Text = regions == 1 ? "1 difference region" : $"{regions:N0} difference regions";
        _verdict.ClearValue(TextBlock.ForegroundProperty);
        var pixels = result.DifferenceCount == 1 ? "1 pixel differs" : $"{result.DifferenceCount:N0} pixels differ";
        var regionText = regions == 1 ? "1 region" : $"{regions:N0} regions";
        var position = _currentRegion >= 0 ? $"  ·  region {_currentRegion + 1} of {regions:N0}" : string.Empty;
        _summary.Text = $"{pixels} ({FormatPercentage(result.DifferencePercentage)}) in {regionText}; {sizes}{position}";
    }

    private static string FormatPercentage(double percentage) =>
        percentage > 0 && percentage < 0.1 ? "<0.1%" : percentage.ToString("0.#", CultureInfo.CurrentCulture) + "%";

    private static string SizeText(int width, int height) => $"{width:N0}×{height:N0}";

    /// <summary>Decodes both files; runs on a background thread, so every bitmap it returns is frozen.</summary>
    private static LoadedImages LoadImages(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        var left = Decode(leftPath);
        cancellationToken.ThrowIfCancellationRequested();
        var right = Decode(rightPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new LoadedImages(left.Pixels, right.Pixels, left.Bitmap, right.Bitmap);
    }

    private static (PixelImage Pixels, BitmapSource Bitmap) Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(PickFrame(decoder), PixelFormats.Bgra32, null, 0);
            var stride = checked(converted.PixelWidth * PixelImage.BytesPerPixel);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var image = new PixelImage(converted.PixelWidth, converted.PixelHeight, stride, pixels);
            return (image, ToBitmap(image));
        }
        catch (NotSupportedException)
        {
            throw new NotSupportedException($"Windows has no decoder for {Path.GetFileName(path)}. WebP and some other formats need an image extension from the Microsoft Store.");
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is too large to compare.");
        }
    }

    /// <summary>The first frame (GIF, TIFF), except for icons, where the largest size is the most useful.</summary>
    private static BitmapFrame PickFrame(BitmapDecoder decoder)
    {
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The file holds no image.");
        }

        return decoder is IconBitmapDecoder
            ? decoder.Frames.OrderByDescending(frame => (long)frame.PixelWidth * frame.PixelHeight).First()
            : decoder.Frames[0];
    }

    private static BitmapSource ToBitmap(PixelImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- Views ----

    private void OnModeToggled(ImageViewMode mode, bool isOn)
    {
        if (_updatingModes)
        {
            return;
        }

        if (isOn)
        {
            SetMode(mode);
        }
        else if (mode == _mode)
        {
            // The toggles act as one radio group: clicking the current view keeps it selected.
            _updatingModes = true;
            _modeToggles[mode].IsChecked = true;
            _updatingModes = false;
        }
    }

    private void SetMode(ImageViewMode mode)
    {
        _mode = mode;
        _updatingModes = true;
        foreach (var pair in _modeToggles)
        {
            pair.Value.IsChecked = pair.Key == mode;
        }

        _updatingModes = false;
        ShowMode();
    }

    /// <summary>Fills the panes with the layers of the current view.</summary>
    private void ShowMode()
    {
        var sideBySide = _mode == ImageViewMode.SideBySide;
        _body.ColumnDefinitions[2].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _secondary.Scroller.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
        _paneDivider.Visibility = _secondary.Scroller.Visibility;
        _blendGroup!.Visibility = _mode == ImageViewMode.Blend ? Visibility.Visible : Visibility.Collapsed;
        _swipeGroup!.Visibility = _mode == ImageViewMode.Swipe ? Visibility.Visible : Visibility.Collapsed;
        _leader = _primary;

        foreach (var pane in Panes)
        {
            pane.Layers.Children.Clear();
        }

        if (_loaded is { } loaded && _result is { } result)
        {
            AddLayers(loaded, result);
        }

        RefreshOutlines();
        if (_fitToWindow)
        {
            UpdateLayout();
            ApplyFit();
        }
        else
        {
            MirrorScroll(_primary);
        }
    }

    private void AddLayers(LoadedImages loaded, ImageDiffResult result)
    {
        switch (_mode)
        {
            case ImageViewMode.SideBySide:
                _primary.Layers.Children.Add(PlacedImage(loaded.LeftBitmap, result.LeftX, result.LeftY));
                _secondary.Layers.Children.Add(PlacedImage(loaded.RightBitmap, result.RightX, result.RightY));
                break;
            case ImageViewMode.Blend:
                _primary.Layers.Children.Add(PlacedImage(loaded.LeftBitmap, result.LeftX, result.LeftY));
                _primary.Layers.Children.Add(PlacedImage(loaded.RightBitmap, result.RightX, result.RightY));
                UpdateBlend();
                break;
            case ImageViewMode.Swipe:
                // The right image underneath; the left one on top, clipped to the slider (see UpdateSwipe).
                _primary.Layers.Children.Add(PlacedImage(loaded.RightBitmap, result.RightX, result.RightY));
                _primary.Layers.Children.Add(new Grid { Children = { PlacedImage(loaded.LeftBitmap, result.LeftX, result.LeftY) } });
                UpdateSwipe();
                break;
            default:
                if (_overlays.TryGetValue(_mode, out var overlay))
                {
                    _primary.Layers.Children.Add(PlacedImage(overlay, 0, 0));
                }
                else
                {
                    RenderOverlay(_mode, loaded, result);
                }

                break;
        }
    }

    private static Image PlacedImage(BitmapSource source, int x, int y) => new()
    {
        Source = source,
        Width = source.PixelWidth,
        Height = source.PixelHeight,
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(x, y, 0, 0),
    };

    private void UpdateBlend()
    {
        if (_mode == ImageViewMode.Blend && _primary.Layers.Children.Count == 2)
        {
            _primary.Layers.Children[1].Opacity = _blendSlider.Value / 100;
        }
    }

    private void UpdateSwipe()
    {
        if (_mode != ImageViewMode.Swipe || _result is not { } result || _primary.Layers.Children.Count != 2)
        {
            return;
        }

        var edge = result.Width * _swipeSlider.Value / 100;
        _primary.Layers.Children[1].Clip = new RectangleGeometry(new Rect(0, 0, edge, result.Height));
        RefreshOutlines();
    }

    /// <summary>Renders the highlight, XOR or subtract picture in the background and shows it when it is ready.</summary>
    private void RenderOverlay(ImageViewMode mode, LoadedImages loaded, ImageDiffResult result)
    {
        if (!_rendering.Add(mode))
        {
            return;
        }

        _details.Text = "Rendering the view…";
        var cancellation = _compareCancellation?.Token ?? CancellationToken.None;
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var bitmap = await Task.Run(() => ToBitmap(RenderPixels(mode, loaded, result)), cancellation);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
                if (!_disposed && ReferenceEquals(result, _result))
                {
                    _overlays[mode] = bitmap;
                    _rendering.Remove(mode);
                    _details.Text = HintText;
                    if (_mode == mode)
                    {
                        ShowMode();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_disposed && ReferenceEquals(result, _result))
                {
                    _rendering.Remove(mode);
                    _details.Text = "The view could not be rendered: " + ex.Message;
                }
            }
        }).FileAndForget("DiffRapid/ImageOverlay");
    }

    private static PixelImage RenderPixels(ImageViewMode mode, LoadedImages loaded, ImageDiffResult result) => mode switch
    {
        ImageViewMode.Highlight => ImageOverlay.Highlight(loaded.Left, loaded.Right, result),
        ImageViewMode.Xor => ImageOverlay.Xor(loaded.Left, loaded.Right, result),
        ImageViewMode.Subtract => ImageOverlay.Subtract(loaded.Left, loaded.Right, result),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Draws the region outlines: every region side by side (the current one in blue), only the current one in
    /// the combined views, and the swipe line. Strokes are scaled so they keep the same width on screen.
    /// </summary>
    private void RefreshOutlines()
    {
        foreach (var pane in Panes)
        {
            pane.Outlines.Children.Clear();
        }

        if (_result is not { } result)
        {
            return;
        }

        var regions = result.DifferenceRegions;
        var sideBySide = _mode == ImageViewMode.SideBySide;
        for (var index = 0; index < regions.Count; index++)
        {
            var current = index == _currentRegion;
            if (!sideBySide && !current)
            {
                continue;
            }

            _primary.Outlines.Children.Add(Outline(regions[index], current));
            if (sideBySide)
            {
                _secondary.Outlines.Children.Add(Outline(regions[index], current));
            }
        }

        if (_mode == ImageViewMode.Swipe)
        {
            _primary.Outlines.Children.Add(SwipeLine(result));
        }
    }

    private Rectangle Outline(ImageRegion region, bool current)
    {
        // The stroke sits just outside the region so it never covers the pixels that differ.
        var thickness = (current ? 2.5 : 1.5) / SurfaceScale;
        var outline = new Rectangle
        {
            Width = region.Width + 2 * thickness,
            Height = region.Height + 2 * thickness,
            Stroke = current ? DiffTheme.MovedBrush : DiffTheme.ChangedBrush,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(outline, region.X - thickness);
        Canvas.SetTop(outline, region.Y - thickness);
        return outline;
    }

    private Rectangle SwipeLine(ImageDiffResult result)
    {
        var width = 2 / SurfaceScale;
        var line = new Rectangle { Width = width, Height = result.Height, Fill = DiffTheme.MovedBrush };
        Canvas.SetLeft(line, result.Width * _swipeSlider.Value / 100 - width / 2);
        Canvas.SetTop(line, 0);
        return line;
    }

    // ---- Difference regions ----

    /// <summary>Moves to the next (+1) or previous (-1) difference region, wrapping around, and centres it.</summary>
    private void GoToRegion(int step)
    {
        if (_result is not { } result || result.DifferenceRegions.Count == 0)
        {
            return;
        }

        var count = result.DifferenceRegions.Count;
        _currentRegion = _currentRegion < 0
            ? (step > 0 ? 0 : count - 1)
            : (_currentRegion + step + count) % count;
        RefreshOutlines();
        CentreOn(result.DifferenceRegions[_currentRegion]);
        UpdateSummary();
    }

    /// <summary>Scrolls a region to the middle of the view, zooming out first if it would not fit.</summary>
    private void CentreOn(ImageRegion region)
    {
        var scroller = _primary.Scroller;
        var viewWidth = scroller.ViewportWidth;
        var viewHeight = scroller.ViewportHeight;
        if (viewWidth > 0 && viewHeight > 0 && (region.Width * SurfaceScale > viewWidth * 0.9 || region.Height * SurfaceScale > viewHeight * 0.9))
        {
            _fitToWindow = false;
            _zoom = ClampZoom(Math.Min(viewWidth * 0.8 / region.Width, viewHeight * 0.8 / region.Height) * _dpiScale);
            ApplyZoom();
        }

        _leader = _primary;
        scroller.UpdateLayout();
        var centre = _primary.Surface.TranslatePoint(new Point(region.X + region.Width / 2.0, region.Y + region.Height / 2.0), scroller);
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + centre.X - scroller.ViewportWidth / 2);
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + centre.Y - scroller.ViewportHeight / 2);
    }

    // ---- Zoom, pan and scroll sync ----

    private static double ClampZoom(double zoom) => Math.Max(MinZoom, Math.Min(MaxZoom, zoom));

    private void ApplyZoom()
    {
        var scale = SurfaceScale;
        var scalingMode = _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        foreach (var pane in Panes)
        {
            pane.Scale.ScaleX = scale;
            pane.Scale.ScaleY = scale;
            RenderOptions.SetBitmapScalingMode(pane.Surface, scalingMode);
        }

        RefreshOutlines();
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        _zoomValue.Text = (_zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
        _zoomValue.ToolTip = _fitToWindow ? "Zoom (fitted to the window)" : "Zoom";
    }

    private void FitToWindow()
    {
        _fitToWindow = true;
        ApplyFit();
    }

    /// <summary>Zooms so the whole canvas fits the visible pane.</summary>
    private void ApplyFit()
    {
        var viewWidth = _primary.Scroller.ActualWidth - 2;
        var viewHeight = _primary.Scroller.ActualHeight - 2;
        if (_result is not { } result || result.Width == 0 || result.Height == 0 || viewWidth <= 0 || viewHeight <= 0)
        {
            UpdateZoomText();
            return;
        }

        _zoom = ClampZoom(Math.Min(viewWidth / result.Width, viewHeight / result.Height) * _dpiScale);
        ApplyZoom();
    }

    private void ZoomBy(double factor) => SetZoomAtCentre(_zoom * factor);

    private void SetZoomAtCentre(double zoom)
    {
        var scroller = _leader.Scroller;
        ZoomAround(_leader, zoom, new Point(scroller.ViewportWidth / 2, scroller.ViewportHeight / 2));
    }

    /// <summary>Changes the zoom keeping the image point under <paramref name="viewportPoint"/> where it is.</summary>
    private void ZoomAround(ImagePane pane, double zoom, Point viewportPoint)
    {
        _fitToWindow = false;
        _leader = pane;
        var anchor = pane.Scroller.TranslatePoint(viewportPoint, pane.Surface);
        _zoom = ClampZoom(zoom);
        ApplyZoom();
        pane.Scroller.UpdateLayout();
        var moved = pane.Surface.TranslatePoint(anchor, pane.Scroller);
        pane.Scroller.ScrollToHorizontalOffset(pane.Scroller.HorizontalOffset + moved.X - viewportPoint.X);
        pane.Scroller.ScrollToVerticalOffset(pane.Scroller.VerticalOffset + moved.Y - viewportPoint.Y);
    }

    private void OnMouseWheel(ImagePane pane, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        ZoomAround(pane, _zoom * factor, e.GetPosition(pane.Scroller));
        e.Handled = true;
    }

    /// <summary>Side by side, the pane the user works in leads and the other follows its scroll position.</summary>
    private void MirrorScroll(ImagePane source)
    {
        if (source != _leader || _mode != ImageViewMode.SideBySide)
        {
            return;
        }

        var follower = source == _primary ? _secondary : _primary;
        follower.Scroller.ScrollToHorizontalOffset(source.Scroller.HorizontalOffset);
        follower.Scroller.ScrollToVerticalOffset(source.Scroller.VerticalOffset);
    }

    private void BeginPan(ImagePane pane, MouseButtonEventArgs e)
    {
        Focus();
        _leader = pane;
        pane.PanStart = e.GetPosition(pane.Scroller);
        pane.PanOffset = new Point(pane.Scroller.HorizontalOffset, pane.Scroller.VerticalOffset);
        pane.Backdrop.CaptureMouse();
        pane.Backdrop.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void EndPan(ImagePane pane)
    {
        pane.PanStart = null;
        pane.Backdrop.ReleaseMouseCapture();
        pane.Backdrop.ClearValue(CursorProperty);
    }

    private void OnPaneMouseMove(ImagePane pane, MouseEventArgs e)
    {
        if (pane.PanStart is { } start)
        {
            var position = e.GetPosition(pane.Scroller);
            pane.Scroller.ScrollToHorizontalOffset(pane.PanOffset.X - (position.X - start.X));
            pane.Scroller.ScrollToVerticalOffset(pane.PanOffset.Y - (position.Y - start.Y));
            return;
        }

        ShowPixelAt(e.GetPosition(pane.Surface));
    }

    /// <summary>Shows the canvas position under the mouse and both images' colours there.</summary>
    private void ShowPixelAt(Point point)
    {
        if (_loaded is not { } loaded || _result is not { } result)
        {
            return;
        }

        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= result.Width || y >= result.Height)
        {
            _details.Text = HintText;
            return;
        }

        var left = ColourText(loaded.Left, x - result.LeftX, y - result.LeftY);
        var right = ColourText(loaded.Right, x - result.RightX, y - result.RightY);
        var kind = result.GetKind(x, y) switch
        {
            PixelDiffKind.Same => "same",
            PixelDiffKind.Different => "differs",
            PixelDiffKind.LeftOnly => "left only",
            PixelDiffKind.RightOnly => "right only",
            _ => "outside both",
        };
        _details.Text = $"{x}, {y}  ·  left {left}  ·  right {right}  ·  {kind}";
    }

    private static string ColourText(PixelImage image, int x, int y)
    {
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
        {
            return "—";
        }

        var colour = image.GetPixel(x, y);
        return $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}" + (colour.A == 255 ? string.Empty : $" α{colour.A}");
    }

    // ---- Keyboard and commands ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.F5:
                StartCompare(reload: true);
                break;
            case Key.F7:
                GoToRegion(shift ? -1 : +1);
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomBy(ZoomStep);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomBy(1 / ZoomStep);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (OwnCommand(pguidCmdGroup, prgCmds[0].cmdID) is not null)
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (OwnCommand(pguidCmdGroup, nCmdID) is { } command)
        {
            command();
            return VSConstants.S_OK;
        }

        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    // The DiffRapid next/previous difference commands, plus View.Code (F7) and View.Designer (Shift+F7):
    // Visual Studio may turn those keys into commands before the view sees them, and an image comparison has
    // no code or designer to show, so they step through the difference regions instead.
    private Action? OwnCommand(Guid group, uint id)
    {
        if (group == PackageIds.CommandSet)
        {
            return (int)id switch
            {
                PackageIds.PreviousDifference => () => GoToRegion(-1),
                PackageIds.NextDifference => () => GoToRegion(+1),
                _ => null,
            };
        }

        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            return (VSConstants.VSStd97CmdID)id switch
            {
                VSConstants.VSStd97CmdID.ViewCode => () => GoToRegion(+1),
                VSConstants.VSStd97CmdID.ViewForm => () => GoToRegion(-1),
                _ => null,
            };
        }

        return null;
    }

    private static Brush CreateCheckerboard()
    {
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x38, 0x80, 0x80, 0x80)), null, squares);
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 16, 16),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Both decoded images: pixels for comparing, frozen bitmaps for showing.</summary>
    private sealed class LoadedImages
    {
        public LoadedImages(PixelImage left, PixelImage right, BitmapSource leftBitmap, BitmapSource rightBitmap)
        {
            Left = left;
            Right = right;
            LeftBitmap = leftBitmap;
            RightBitmap = rightBitmap;
        }

        public PixelImage Left { get; }

        public PixelImage Right { get; }

        public BitmapSource LeftBitmap { get; }

        public BitmapSource RightBitmap { get; }
    }

    /// <summary>
    /// One scrollable picture: a checkerboard backdrop around a surface the size of the comparison canvas,
    /// scaled by the zoom, holding the image layers and, above them, the region outlines.
    /// </summary>
    private sealed class ImagePane
    {
        public ImagePane()
        {
            Surface = new Grid
            {
                LayoutTransform = Scale,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Children = { Layers, Outlines },
            };
            Backdrop = new Border { Child = Surface, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Scroller = new ScrollViewer
            {
                Content = Backdrop,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            };
        }

        public ScaleTransform Scale { get; } = new();

        public Grid Layers { get; } = new();

        public Canvas Outlines { get; } = new() { IsHitTestVisible = false };

        public Grid Surface { get; }

        public Border Backdrop { get; }

        public ScrollViewer Scroller { get; }

        /// <summary>Where a drag started, in viewport coordinates, while the user pans.</summary>
        public Point? PanStart { get; set; }

        /// <summary>The scroll offsets when the drag started.</summary>
        public Point PanOffset { get; set; }
    }
}

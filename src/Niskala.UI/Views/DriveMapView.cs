using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Niskala.Optimizer.Drives;

namespace Niskala.UI.Views;

public readonly record struct MapGrid(int Columns, int Rows, int Cell, int Gap, int PixelWidth, int PixelHeight)
{
    public const double CellSize = 9;
    public const double CellGap = 2;

    public int Cells => Columns * Rows;

    public int Pitch => Cell + Gap;

    public static MapGrid For(double width, double height, double scale)
    {
        if (width <= 0 || height <= 0 || scale <= 0)
        {
            return default;
        }

        int cell = Math.Max(2, (int)Math.Round(CellSize * scale));
        int gap = Math.Max(1, (int)Math.Round(CellGap * scale));
        int pitch = cell + gap;
        int columns = Math.Max(1, ((int)Math.Floor(width * scale) + gap) / pitch);
        int rows = Math.Max(1, ((int)Math.Floor(height * scale) + gap) / pitch);
        return new MapGrid(columns, rows, cell, gap, (columns * pitch) - gap, (rows * pitch) - gap);
    }
}

public sealed partial class DriveMapView : Grid
{
    public static readonly DependencyProperty FreeBrushProperty = Register(nameof(FreeBrush));
    public static readonly DependencyProperty UsedBrushProperty = Register(nameof(UsedBrush));
    public static readonly DependencyProperty FragmentedBrushProperty = Register(nameof(FragmentedBrush));
    public static readonly DependencyProperty ArchiveBrushProperty = Register(nameof(ArchiveBrush));
    public static readonly DependencyProperty SystemBrushProperty = Register(nameof(SystemBrush));

    private const int BytesPerPixel = 4;

    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private Func<int, ClusterState[]>? _source;
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];

    public DriveMapView()
    {
        Children.Add(_image);
        SizeChanged += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += OnRootChanged;
            }

            Render();
        };
        Unloaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed -= OnRootChanged;
            }
        };
    }

    public Brush? FreeBrush
    {
        get => (Brush?)GetValue(FreeBrushProperty);
        set => SetValue(FreeBrushProperty, value);
    }

    public Brush? UsedBrush
    {
        get => (Brush?)GetValue(UsedBrushProperty);
        set => SetValue(UsedBrushProperty, value);
    }

    public Brush? FragmentedBrush
    {
        get => (Brush?)GetValue(FragmentedBrushProperty);
        set => SetValue(FragmentedBrushProperty, value);
    }

    public Brush? ArchiveBrush
    {
        get => (Brush?)GetValue(ArchiveBrushProperty);
        set => SetValue(ArchiveBrushProperty, value);
    }

    public Brush? SystemBrush
    {
        get => (Brush?)GetValue(SystemBrushProperty);
        set => SetValue(SystemBrushProperty, value);
    }

    public void Show(Func<int, ClusterState[]>? source)
    {
        _source = source;
        Render();
    }

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(DriveMapView), new PropertyMetadata(null, (d, _) => ((DriveMapView)d).Render()));

    private static uint Pixel(Brush? brush) => brush is SolidColorBrush { Color: var color }
        ? (uint)((color.A << 24) | (color.R * color.A / 255 << 16) | (color.G * color.A / 255 << 8) | (color.B * color.A / 255))
        : 0;

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Render();

    private void Render()
    {
        if (XamlRoot is not { } root || !IsLoaded)
        {
            return;
        }

        double scale = root.RasterizationScale;
        MapGrid grid = MapGrid.For(ActualWidth, ActualHeight, scale);
        ClusterState[] states = grid.Cells > 0 && _source is { } source ? source(grid.Cells) : [];
        if (states.Length == 0)
        {
            _image.Source = null;
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != grid.PixelWidth || _bitmap.PixelHeight != grid.PixelHeight)
        {
            _bitmap = new WriteableBitmap(grid.PixelWidth, grid.PixelHeight);
            _pixels = new byte[grid.PixelWidth * grid.PixelHeight * BytesPerPixel];
        }

        Span<uint> palette = [Pixel(FreeBrush), Pixel(UsedBrush), Pixel(FragmentedBrush), Pixel(ArchiveBrush), Pixel(SystemBrush)];
        Span<uint> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan());
        pixels.Clear();
        for (int index = 0; index < states.Length && index < grid.Cells; index++)
        {
            uint color = palette[(int)states[index] % palette.Length];
            int left = index % grid.Columns * grid.Pitch;
            int top = index / grid.Columns * grid.Pitch;
            for (int y = top; y < top + grid.Cell; y++)
            {
                pixels.Slice((y * grid.PixelWidth) + left, grid.Cell).Fill(color);
            }
        }

        _pixels.CopyTo(_bitmap.PixelBuffer);
        _bitmap.Invalidate();
        _image.Source = _bitmap;
        _image.Width = grid.PixelWidth / scale;
        _image.Height = grid.PixelHeight / scale;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace HeavyJobQueue.App;

internal sealed class PerformanceHistoryControl : FrameworkElement
{
    private const int HistoryLength = 60;
    private const double HeaderHeight = 24;
    private const double MemoryHeight = 52;
    private const double Gap = 4;
    private static readonly Brush BackgroundBrush = Brush("#101820");
    private static readonly Brush GraphBackgroundBrush = Brush("#062C35");
    private static readonly Brush PrimaryTextBrush = Brush("#F4F7FA");
    private static readonly Brush SecondaryTextBrush = Brush("#A9BCC4");
    private static readonly Pen BorderPen = Pen("#4A7781", 1);
    private static readonly Pen GridPen = Pen("#174854", 0.5);
    private static readonly Pen CpuPen = Pen("#35C5F0", 1);
    private static readonly Pen MemoryPen = Pen("#B785F4", 1.5);

    private readonly List<Queue<double>> _processorHistory = [];
    private readonly Queue<double> _memoryHistory = new();
    private string? _error;
    private ulong _usedMemory;
    private ulong _totalMemory;

    public void AddSample(SystemPerformanceSample sample)
    {
        _error = null;
        EnsureProcessorCount(sample.ProcessorUtilization.Count);
        for (var index = 0; index < sample.ProcessorUtilization.Count; index++)
        {
            AddHistoryValue(
                _processorHistory[index],
                sample.ProcessorUtilization[index]);
        }

        _usedMemory = sample.UsedPhysicalMemory;
        _totalMemory = sample.TotalPhysicalMemory;
        AddHistoryValue(
            _memoryHistory,
            _totalMemory == 0 ? 0 : (double)_usedMemory / _totalMemory);
        InvalidateVisual();
    }

    public void ShowError(string message)
    {
        _error = message;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(
            BackgroundBrush,
            null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (_error is not null)
        {
            DrawText(
                drawingContext,
                $"System utilization unavailable: {_error}",
                new Point(10, 10),
                13,
                PrimaryTextBrush);
            return;
        }

        if (_processorHistory.Count == 0 || ActualWidth < 100 || ActualHeight < 120)
        {
            DrawText(
                drawingContext,
                "Collecting system utilization...",
                new Point(10, 10),
                13,
                SecondaryTextBrush);
            return;
        }

        var average = _processorHistory.Average(
            history => history.Count == 0 ? 0 : history.Last());
        DrawText(
            drawingContext,
            $"CPU  {average:P0}   {_processorHistory.Count} logical processors   60 seconds",
            new Point(8, 3),
            13,
            PrimaryTextBrush);

        var cpuHeight = ActualHeight - HeaderHeight - MemoryHeight - (Gap * 2);
        var columns = Math.Clamp(
            (int)Math.Ceiling(Math.Sqrt(
                _processorHistory.Count * ActualWidth / Math.Max(cpuHeight, 1))),
            1,
            _processorHistory.Count);
        var rows = (int)Math.Ceiling((double)_processorHistory.Count / columns);
        var cellWidth = (ActualWidth - ((columns + 1) * Gap)) / columns;
        var cellHeight = (cpuHeight - ((rows + 1) * Gap)) / rows;

        for (var index = 0; index < _processorHistory.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var bounds = new Rect(
                Gap + (column * (cellWidth + Gap)),
                HeaderHeight + Gap + (row * (cellHeight + Gap)),
                cellWidth,
                cellHeight);
            DrawGraph(drawingContext, bounds, _processorHistory[index], CpuPen);
            DrawText(
                drawingContext,
                index.ToString(CultureInfo.InvariantCulture),
                new Point(bounds.X + 3, bounds.Y + 1),
                8,
                SecondaryTextBrush);
        }

        var memoryBounds = new Rect(
            Gap,
            ActualHeight - MemoryHeight,
            ActualWidth - (Gap * 2),
            MemoryHeight - Gap);
        DrawGraph(drawingContext, memoryBounds, _memoryHistory, MemoryPen);
        DrawText(
            drawingContext,
            $"Memory  {FormatBytes(_usedMemory)} / {FormatBytes(_totalMemory)}" +
            $"  ({(_totalMemory == 0 ? 0 : (double)_usedMemory / _totalMemory):P0})",
            new Point(memoryBounds.X + 6, memoryBounds.Y + 3),
            11,
            PrimaryTextBrush);
    }

    private static void DrawGraph(
        DrawingContext drawingContext,
        Rect bounds,
        IReadOnlyCollection<double> history,
        Pen linePen)
    {
        drawingContext.DrawRectangle(GraphBackgroundBrush, BorderPen, bounds);

        for (var index = 1; index < 4; index++)
        {
            var x = bounds.X + (bounds.Width * index / 4);
            var y = bounds.Y + (bounds.Height * index / 4);
            drawingContext.DrawLine(
                GridPen,
                new Point(x, bounds.Y),
                new Point(x, bounds.Bottom));
            drawingContext.DrawLine(
                GridPen,
                new Point(bounds.X, y),
                new Point(bounds.Right, y));
        }

        if (history.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var values = history.ToArray();
            var interval = bounds.Width / (HistoryLength - 1);
            var startX = bounds.Right - ((values.Length - 1) * interval);
            context.BeginFigure(
                new Point(startX, ValueY(bounds, values[0])),
                isFilled: false,
                isClosed: false);
            for (var index = 1; index < values.Length; index++)
            {
                context.LineTo(
                    new Point(
                        startX + (index * interval),
                        ValueY(bounds, values[index])),
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, linePen, geometry);
    }

    private static double ValueY(Rect bounds, double value) =>
        bounds.Bottom - (Math.Clamp(value, 0, 1) * bounds.Height);

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        Point location,
        double size,
        Brush brush)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(formattedText, location);
    }

    private void EnsureProcessorCount(int count)
    {
        if (_processorHistory.Count == count)
        {
            return;
        }

        _processorHistory.Clear();
        for (var index = 0; index < count; index++)
        {
            _processorHistory.Add(new Queue<double>());
        }
    }

    private static void AddHistoryValue(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength)
        {
            history.Dequeue();
        }
    }

    private static string FormatBytes(ulong bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(string color, double thickness)
    {
        var pen = new Pen(Brush(color), thickness);
        pen.Freeze();
        return pen;
    }
}

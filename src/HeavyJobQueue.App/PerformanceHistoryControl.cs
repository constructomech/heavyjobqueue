using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
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
        var backgroundBrush = ResourceBrush("MonitorBackgroundBrush");
        var graphBrush = ResourceBrush("MonitorGraphBrush");
        var primaryTextBrush = ResourceBrush("MonitorPrimaryTextBrush");
        var secondaryTextBrush = ResourceBrush("MonitorSecondaryTextBrush");
        var borderPen = CreatePen(ResourceBrush("BorderBrush"), 1);
        var gridPen = CreatePen(ResourceBrush("MonitorGridBrush"), 0.5);
        var cpuPen = CreatePen(ResourceBrush("MonitorCpuBrush"), 1);
        var memoryPen = CreatePen(ResourceBrush("MonitorMemoryBrush"), 1.5);

        drawingContext.DrawRectangle(
            backgroundBrush,
            null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (_error is not null)
        {
            DrawText(
                drawingContext,
                $"System utilization unavailable: {_error}",
                new Point(10, 10),
                13,
                primaryTextBrush);
            return;
        }

        if (_processorHistory.Count == 0 || ActualWidth < 100 || ActualHeight < 120)
        {
            DrawText(
                drawingContext,
                "Collecting system utilization...",
                new Point(10, 10),
                13,
                secondaryTextBrush);
            return;
        }

        var average = _processorHistory.Average(
            history => history.Count == 0 ? 0 : history.Last());
        DrawText(
            drawingContext,
            $"CPU  {average:P0}   {_processorHistory.Count} logical processors   60 seconds",
            new Point(8, 3),
            13,
            primaryTextBrush);

        var cpuHeight = ActualHeight - HeaderHeight - MemoryHeight - (Gap * 2);
        var maximumColumns = Math.Max(1, (int)(ActualWidth / 72));
        var rows = (int)Math.Ceiling(
            (double)_processorHistory.Count / maximumColumns);
        var columns = (int)Math.Ceiling(
            (double)_processorHistory.Count / rows);
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
            DrawGraph(
                drawingContext,
                bounds,
                _processorHistory[index],
                cpuPen,
                graphBrush,
                borderPen,
                gridPen);
            DrawText(
                drawingContext,
                index.ToString(CultureInfo.InvariantCulture),
                new Point(bounds.X + 3, bounds.Y + 1),
                8,
                secondaryTextBrush);
        }

        var memoryBounds = new Rect(
            Gap,
            ActualHeight - MemoryHeight,
            ActualWidth - (Gap * 2),
            MemoryHeight - Gap);
        DrawGraph(
            drawingContext,
            memoryBounds,
            _memoryHistory,
            memoryPen,
            graphBrush,
            borderPen,
            gridPen);
        DrawText(
            drawingContext,
            $"Memory  {FormatBytes(_usedMemory)} / {FormatBytes(_totalMemory)}" +
            $"  ({(_totalMemory == 0 ? 0 : (double)_usedMemory / _totalMemory):P0})",
            new Point(memoryBounds.X + 6, memoryBounds.Y + 3),
            11,
            primaryTextBrush);
    }

    private static void DrawGraph(
        DrawingContext drawingContext,
        Rect bounds,
        IReadOnlyCollection<double> history,
        Pen linePen,
        Brush graphBrush,
        Pen borderPen,
        Pen gridPen)
    {
        drawingContext.DrawRectangle(graphBrush, borderPen, bounds);

        for (var index = 1; index < 4; index++)
        {
            var x = bounds.X + (bounds.Width * index / 4);
            var y = bounds.Y + (bounds.Height * index / 4);
            drawingContext.DrawLine(
                gridPen,
                new Point(x, bounds.Y),
                new Point(x, bounds.Bottom));
            drawingContext.DrawLine(
                gridPen,
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

    private Brush ResourceBrush(string key) =>
        (Brush)FindResource(key);

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}

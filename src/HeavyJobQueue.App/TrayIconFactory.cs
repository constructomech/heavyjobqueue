using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HeavyJobQueue.App;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        using var background = RoundedRectangle(1, 1, 30, 30, 7);
        using var backgroundBrush = new LinearGradientBrush(
            new Point(2, 2),
            new Point(30, 30),
            Color.FromArgb(31, 111, 235),
            Color.FromArgb(89, 54, 180));
        graphics.FillPath(backgroundBrush, background);

        using var activeBrush = new SolidBrush(Color.FromArgb(255, 203, 71));
        using var waitingBrush = new SolidBrush(Color.FromArgb(222, 234, 255));
        using var lineBrush = new SolidBrush(Color.White);

        graphics.FillEllipse(activeBrush, 6, 7, 6, 6);
        graphics.FillEllipse(waitingBrush, 6, 17, 5, 5);
        graphics.FillEllipse(waitingBrush, 6, 25, 4, 4);

        graphics.FillRoundedRectangle(lineBrush, 15, 8, 11, 4, 2);
        graphics.FillRoundedRectangle(waitingBrush, 14, 17, 12, 4, 2);
        graphics.FillRoundedRectangle(waitingBrush, 13, 25, 10, 3, 1.5f);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(
        float x,
        float y,
        float width,
        float height,
        float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        float x,
        float y,
        float width,
        float height,
        float radius)
    {
        using var path = RoundedRectangle(x, y, width, height, radius);
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

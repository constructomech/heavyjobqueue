using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageSource = System.Windows.Media.ImageSource;
using Int32Rect = System.Windows.Int32Rect;

namespace HeavyJobQueue.App;

internal static class TrayIconFactory
{
    private static readonly Uri IconUri =
        new("pack://application:,,,/Assets/HeavyJobQueue.ico", UriKind.Absolute);

    public static Icon Create()
    {
        var resource = System.Windows.Application.GetResourceStream(IconUri)
            ?? throw new InvalidOperationException("Heavy Job Queue icon resource is missing.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static ImageSource CreateImageSource()
    {
        using var icon = Create();
        var image = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        image.Freeze();
        return image;
    }
}

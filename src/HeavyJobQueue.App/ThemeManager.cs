using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Window = System.Windows.Window;

namespace HeavyJobQueue.App;

internal static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    public static bool IsDarkMode { get; private set; }

    public static void Apply(Application application)
    {
        IsDarkMode = ReadDarkMode();
        var colors = IsDarkMode ? DarkColors : LightColors;
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            application.Resources[key] = brush;
        }
    }

    public static void ApplyWindowChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = IsDarkMode ? 1 : 0;
        if (DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }

    private static bool ReadDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#F5F7FA",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceAltBrush"] = "#F0F3F6",
            ["PrimaryTextBrush"] = "#17212B",
            ["SecondaryTextBrush"] = "#53626F",
            ["BorderBrush"] = "#9BAEB8",
            ["GridLineBrush"] = "#CDD7DC",
            ["HeaderBrush"] = "#E4EBEF",
            ["SelectionBrush"] = "#C9EAF5",
            ["ButtonBrush"] = "#E5EDF1",
            ["ButtonHoverBrush"] = "#D5E4EA",
            ["ButtonPressedBrush"] = "#C3D8E0",
            ["WarningButtonBrush"] = "#F6DCA8",
            ["ToolTipBrush"] = "#FFFFFF",
            ["MonitorBackgroundBrush"] = "#F7FAFC",
            ["MonitorGraphBrush"] = "#E8F7FA",
            ["MonitorGridBrush"] = "#B9DDE5",
            ["MonitorCpuBrush"] = "#0078A8",
            ["MonitorMemoryBrush"] = "#7A3FC1",
            ["MonitorPrimaryTextBrush"] = "#17212B",
            ["MonitorSecondaryTextBrush"] = "#53626F"
        };

    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#101820",
            ["SurfaceBrush"] = "#16242D",
            ["SurfaceAltBrush"] = "#0D1F27",
            ["PrimaryTextBrush"] = "#F4F7FA",
            ["SecondaryTextBrush"] = "#A9BCC4",
            ["BorderBrush"] = "#365965",
            ["GridLineBrush"] = "#284955",
            ["HeaderBrush"] = "#17313B",
            ["SelectionBrush"] = "#255E73",
            ["ButtonBrush"] = "#223A45",
            ["ButtonHoverBrush"] = "#2F5361",
            ["ButtonPressedBrush"] = "#1B3039",
            ["WarningButtonBrush"] = "#6A4A16",
            ["ToolTipBrush"] = "#1B2B34",
            ["MonitorBackgroundBrush"] = "#101820",
            ["MonitorGraphBrush"] = "#062C35",
            ["MonitorGridBrush"] = "#174854",
            ["MonitorCpuBrush"] = "#35C5F0",
            ["MonitorMemoryBrush"] = "#B785F4",
            ["MonitorPrimaryTextBrush"] = "#F4F7FA",
            ["MonitorSecondaryTextBrush"] = "#A9BCC4"
        };
}

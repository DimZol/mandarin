using System.Windows;
using System.Windows.Media;
using Mandarin.Core;
using Microsoft.Win32;

namespace Mandarin.UI;

/// <summary>Mandarin colors and light/dark theme detection.</summary>
public static class Theme
{
    public static readonly Color Accent = Color.FromRgb(0xF2, 0x6B, 0x1D);
    public static readonly Color AccentLight = Color.FromRgb(0xFF, 0x8A, 0x3D);
    public static readonly Color AccentDark = Color.FromRgb(0xD9, 0x56, 0x0C);

    public static bool IsDark
    {
        get
        {
            return AppSettings.Current.Theme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                _ => SystemIsDark()
            };
        }
    }

    static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => Brush(Color.FromArgb(a, r, g, b));

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>Wheel palette for the current theme.</summary>
    public sealed record WheelColors(Brush Ring, Brush RingStroke, Brush Sector, Brush SectorStroke, Brush Text, Brush SubText,
        Brush Center, Brush Hover, Brush HoverText, Brush Pill, Brush PillText, Brush Disabled);

    public static WheelColors Wheel => IsDark
        ? new WheelColors(
            Brush(0x26, 0x26, 0x26, 0xF2), Brush(0x3A, 0x3A, 0x3A), Brush(0x33, 0x33, 0x33), Brush(0x40, 0x40, 0x40),
            Brush(0xF2, 0xF2, 0xF2), Brush(0xA8, 0xA8, 0xA8), Brush(0x2C, 0x2C, 0x2C), Brush(Accent), Brush(0xFF, 0xFF, 0xFF),
            Brush(0x2C, 0x2C, 0x2C, 0xF5), Brush(0xF2, 0xF2, 0xF2), Brush(0x6A, 0x6A, 0x6A))
        : new WheelColors(
            Brush(0xEC, 0xEC, 0xEC, 0xF5), Brush(0xD8, 0xD8, 0xD8), Brush(0xFB, 0xFB, 0xFB), Brush(0xE2, 0xE2, 0xE2),
            Brush(0x26, 0x26, 0x26), Brush(0x6B, 0x6B, 0x6B), Brush(0xFF, 0xFF, 0xFF), Brush(Accent), Brush(0xFF, 0xFF, 0xFF),
            Brush(0xFF, 0xFF, 0xFF, 0xF8), Brush(0x26, 0x26, 0x26), Brush(0xB4, 0xB4, 0xB4));
}

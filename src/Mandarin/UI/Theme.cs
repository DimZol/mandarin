using System.Windows;
using System.Windows.Media;
using Mandarin.Core;
using Microsoft.Win32;

namespace Mandarin.UI;

/// <summary>
/// Interface palette (the same set as in LaserPrep) and the accent color.
/// "Windows" keeps the native look: Mica and the Windows light or dark colors. The other palettes repaint the Fluent theme
/// and Mandarin's own surfaces: every neutral gray of the original is mapped onto the palette ramp, white text and overlays
/// take the palette text color, the system accent colors are replaced with the chosen accent.
/// </summary>
public static class Theme
{
    public const string WindowsId = "windows", AutoId = "auto";

    /// <summary>Palette colors. Accent is the palette's own one, used when the accent color is turned off.</summary>
    public sealed record Palette(string Id, string Name, string Note, bool Dark,
        Color Bg, Color Bg2, Color Panel, Color Panel2, Color Line, Color Line2, Color Text, Color Muted, Color Accent, Color Viewport);

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly Palette[] Palettes =
    {
        new("gray", "Gray", "neutral dark gray", true, C(0x1c1c1c), C(0x212121), C(0x262626), C(0x2d2d2d), C(0x414141), C(0x565656), C(0xe3e3e3), C(0xa1a1a1), C(0xdcdcdc), C(0x181818)),
        new("graphite", "Graphite", "almost black, for a dark room", true, C(0x0f0f0f), C(0x141414), C(0x191919), C(0x1f1f1f), C(0x343434), C(0x4a4a4a), C(0xe8e8e8), C(0x9c9c9c), C(0xe6e6e6), C(0x0a0a0a)),
        new("light", "Light gray", "light theme", false, C(0xe9e9e9), C(0xf2f2f2), C(0xfbfbfb), C(0xececec), C(0xc7c7c7), C(0xadadad), C(0x1d1d1d), C(0x545454), C(0x2e2e2e), C(0xd9d9d9)),
        new("steel", "Steel", "muted gray-blue", true, C(0x181c21), C(0x1c2127), C(0x21272e), C(0x272e36), C(0x39414b), C(0x4f5864), C(0xdfe5ec), C(0x9da7b3), C(0xa9bfd8), C(0x14181c)),
        new("frost", "Teal", "cool blue-green", true, C(0x141d1e), C(0x182224), C(0x1d292b), C(0x233133), C(0x344447), C(0x495d61), C(0xdcebea), C(0x97b1b0), C(0x79cfc6), C(0x101718)),
        new("lilac", "Lilac", "twilight violet", true, C(0x1b1a23), C(0x1f1e29), C(0x25232f), C(0x2b2937), C(0x3d3a4c), C(0x545065), C(0xe5e2ef), C(0xa4a0b6), C(0xb9aeea), C(0x16151c)),
        new("amber", "Amber", "warm golden", true, C(0x1e1b16), C(0x231f19), C(0x29241d), C(0x302a22), C(0x423b31), C(0x5a5144), C(0xece4d6), C(0xb1a591), C(0xe2bb6b), C(0x17140f)),
        new("coffee", "Coffee", "brown, soft", true, C(0x1d1917), C(0x221d1a), C(0x28221e), C(0x2f2824), C(0x423933), C(0x5b5048), C(0xebe1d8), C(0xb2a397), C(0xd4b496), C(0x161210)),
        new("wine", "Burgundy", "wine, muted", true, C(0x1f1719), C(0x251b1e), C(0x2b2023), C(0x33262a), C(0x47363b), C(0x604b52), C(0xefe0e3), C(0xb6a1a6), C(0xe2a3ad), C(0x170f11)),
    };

    /// <summary>Palette groups for the settings page: (title, palette ids).</summary>
    public static readonly (string title, string[] ids)[] Groups =
    {
        ("System palettes", new[] { WindowsId, AutoId }),
        ("Gray palettes", new[] { "gray", "graphite", "light" }),
        ("Cool palettes", new[] { "steel", "frost", "lilac" }),
        ("Warm palettes", new[] { "amber", "coffee", "wine" }),
    };

    public const string DefaultAccent = "#F26B1D";

    /// <summary>Accent presets: Mandarin's own tangerine and the LaserPrep colors.</summary>
    public static readonly (string hex, string name)[] Accents =
    {
        (DefaultAccent, "Tangerine"), ("#4F8CFF", "Blue"), ("#2FA8E0", "Azure"), ("#22B3A6", "Teal"), ("#6F74F0", "Indigo"), ("#9A6CF0", "Violet"),
        ("#3FB56E", "Green"), ("#E5A326", "Amber"), ("#EF7C3A", "Orange"), ("#E8608F", "Pink"), ("#E5534B", "Red"),
    };

    public static Palette? Find(string? id) => Palettes.FirstOrDefault(p => p.Id == id);

    /// <summary>The palette chosen in settings (including "windows" and "auto").</summary>
    public static string ChosenId
    {
        get
        {
            var id = AppSettings.Current.Palette;
            return id == WindowsId || id == AutoId || Find(id) != null ? id! : WindowsId;
        }
    }

    /// <summary>The palette actually painted, or null for the native Windows look.</summary>
    public static Palette? Current => ChosenId switch
    {
        WindowsId => null,
        AutoId => Find(SystemIsDark() ? "gray" : "light"),
        var id => Find(id)
    };

    public static bool IsDark => Current?.Dark ?? SystemIsDark();

    /// <summary>Mica and the native title bar are kept only for the Windows palette.</summary>
    public static bool UsesMica => Current == null;

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ---------------- accent ----------------

    public static bool TryParseHex(string? hex, out Color c)
    {
        c = default;
        if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        c = C(v);
        return true;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The accent from settings, or null when it is turned off (the palette's own accent is used then).</summary>
    public static Color? ChosenAccent =>
        AppSettings.Current.AccentOn ? TryParseHex(AppSettings.Current.AccentColor, out var c) ? c : C(0xF26B1D) : null;

    /// <summary>The accent color of the interface.</summary>
    public static Color Accent
    {
        get
        {
            var p = Current;
            if (ChosenAccent is { } c) return p is { Dark: false } ? Mix(c, Colors.Black, 0.12) : c; // a little darker on the light palette
            return p?.Accent ?? SystemColors.AccentColor;
        }
    }

    public static Color AccentLight => Mix(Accent, Colors.White, 0.18);
    public static Color AccentDark => Mix(Accent, Colors.Black, 0.12);

    /// <summary>Text color on an accent fill: dark on light shades, white on dark ones.</summary>
    public static Color TextOn(Color fill) => Luminance(fill) > 0.36 ? C(0x111111) : Colors.White;

    public static SolidColorBrush OnAccent => Brush(TextOn(Accent));

    static double Luminance(Color c)
    {
        static double L(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    // ---------------- mapping the Windows colors onto the palette ----------------

    /// <summary>
    /// A neutral color of the Windows (Fluent) theme or of Mandarin's own surfaces, repainted with the current palette.
    /// Opaque grays follow the palette ramp by lightness; white text and overlays take the palette text color
    /// (on the light palette black ones do, and white surfaces take the panel color); other colors stay as they are.
    /// </summary>
    public static Color Map(Color c)
    {
        var p = Current;
        if (p == null || c.R != c.G || c.G != c.B) return c;
        if (c.A == 255) return WithAlpha(Ramp(p, c.R), 255);
        if (p.Dark) return c.R == 255 ? WithAlpha(p.Text, c.A) : c;
        return c.R == 0 ? WithAlpha(p.Text, c.A) : c.R == 255 ? WithAlpha(p.Panel, c.A) : c;
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Ramp(Palette p, byte level)
    {
        // gray level of the original → palette color, linear in between
        (byte at, Color to)[] stops = p.Dark
            ? new[] { ((byte)0x00, Mix(p.Viewport, Colors.Black, 0.5)), ((byte)0x0A, p.Viewport), ((byte)0x20, p.Bg), ((byte)0x2C, p.Panel2),
                      ((byte)0x45, p.Line), ((byte)0x56, p.Line2), ((byte)0x9D, p.Muted), ((byte)0xFF, p.Text) }
            : new[] { ((byte)0x00, p.Text), ((byte)0x60, p.Muted), ((byte)0xAD, p.Line2), ((byte)0xD0, p.Line), ((byte)0xF3, p.Bg),
                      ((byte)0xF9, p.Bg2), ((byte)0xFF, p.Panel) };
        for (var i = 1; i < stops.Length; i++)
            if (level <= stops[i].at)
                return Mix(stops[i - 1].to, stops[i].to, (level - stops[i - 1].at) / (double)(stops[i].at - stops[i - 1].at));
        return stops[^1].to;
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>A neutral brush of Mandarin's own surfaces - repainted with the current palette.</summary>
    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => Brush(Map(Color.FromArgb(a, r, g, b)));

    /// <summary>Window background for palettes without Mica.</summary>
    public static SolidColorBrush? WindowBackground => Current is { } p ? Brush(p.Bg) : null;

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>Wheel palette for the current theme.</summary>
    public sealed record WheelColors(Brush Ring, Brush RingStroke, Brush Sector, Brush SectorStroke, Brush Text, Brush SubText,
        Brush Center, Brush Hover, Brush HoverText, Brush Pill, Brush PillText, Brush Disabled);

    public static WheelColors Wheel => IsDark
        ? new WheelColors(
            Brush(0x26, 0x26, 0x26, 0xF2), Brush(0x3A, 0x3A, 0x3A), Brush(0x33, 0x33, 0x33), Brush(0x40, 0x40, 0x40),
            Brush(0xF2, 0xF2, 0xF2), Brush(0xA8, 0xA8, 0xA8), Brush(0x2C, 0x2C, 0x2C), Brush(Accent), OnAccent,
            Brush(0x2C, 0x2C, 0x2C, 0xF5), Brush(0xF2, 0xF2, 0xF2), Brush(0x6A, 0x6A, 0x6A))
        : new WheelColors(
            Brush(0xEC, 0xEC, 0xEC, 0xF5), Brush(0xD8, 0xD8, 0xD8), Brush(0xFB, 0xFB, 0xFB), Brush(0xE2, 0xE2, 0xE2),
            Brush(0x26, 0x26, 0x26), Brush(0x6B, 0x6B, 0x6B), Brush(0xFF, 0xFF, 0xFF), Brush(Accent), OnAccent,
            Brush(0xFF, 0xFF, 0xFF, 0xF8), Brush(0x26, 0x26, 0x26), Brush(0xB4, 0xB4, 0xB4));
}

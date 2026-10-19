using System.Windows;
using System.Windows.Media;

namespace Mandarin.UI;

/// <summary>Tangerine accent for the WPF Fluent theme instead of the system blue.</summary>
public static class Accent
{
    public static void Apply(Application app)
    {
        var res = app.Resources;
        var baseC = Theme.Accent;
        Color L1 = Color.FromRgb(0xF5, 0x82, 0x3D), L2 = Color.FromRgb(0xF8, 0x9A, 0x5E), L3 = Color.FromRgb(0xFB, 0xB5, 0x85);
        Color D1 = Color.FromRgb(0xD9, 0x5A, 0x10), D2 = Color.FromRgb(0xB8, 0x4A, 0x0B), D3 = Color.FromRgb(0x93, 0x3A, 0x07);
        var dark = Theme.IsDark;

        // colors the theme refers to (both by string keys and by SystemColors keys)
        res["SystemAccentColor"] = baseC;
        res["SystemAccentColorLight1"] = L1; res["SystemAccentColorLight2"] = L2; res["SystemAccentColorLight3"] = L3;
        res["SystemAccentColorDark1"] = D1; res["SystemAccentColorDark2"] = D2; res["SystemAccentColorDark3"] = D3;
        res[SystemColors.AccentColorKey] = baseC;
        res[SystemColors.AccentColorLight1Key] = L1; res[SystemColors.AccentColorLight2Key] = L2; res[SystemColors.AccentColorLight3Key] = L3;
        res[SystemColors.AccentColorDark1Key] = D1; res[SystemColors.AccentColorDark2Key] = D2; res[SystemColors.AccentColorDark3Key] = D3;
        res[SystemColors.AccentColorBrushKey] = Brush(baseC);
        res[SystemColors.AccentColorLight1BrushKey] = Brush(L1); res[SystemColors.AccentColorLight2BrushKey] = Brush(L2); res[SystemColors.AccentColorLight3BrushKey] = Brush(L3);
        res[SystemColors.AccentColorDark1BrushKey] = Brush(D1); res[SystemColors.AccentColorDark2BrushKey] = Brush(D2); res[SystemColors.AccentColorDark3BrushKey] = Brush(D3);

        // ready-made theme brushes
        var primary = dark ? L2 : D1;
        res["AccentFillColorDefaultBrush"] = Brush(primary);
        res["AccentFillColorPrimaryBrush"] = Brush(primary);
        res["AccentFillColorSecondaryBrush"] = Brush(Color.FromArgb(0xE6, primary.R, primary.G, primary.B));
        res["AccentFillColorTertiaryBrush"] = Brush(Color.FromArgb(0xCC, primary.R, primary.G, primary.B));
        res["AccentFillColorSelectedTextBackgroundBrush"] = Brush(baseC);
        res["AccentTextFillColorPrimaryBrush"] = Brush(dark ? L3 : D2);
        res["AccentTextFillColorSecondaryBrush"] = Brush(dark ? L3 : D3);
        res["AccentTextFillColorTertiaryBrush"] = Brush(dark ? L2 : D1);
        res["AccentDefaultBrush"] = Brush(primary);
        res["AccentSecondaryBrush"] = Brush(Color.FromArgb(0xE6, primary.R, primary.G, primary.B));
        res["AccentTertiaryBrush"] = Brush(Color.FromArgb(0xCC, primary.R, primary.G, primary.B));
    }

    static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

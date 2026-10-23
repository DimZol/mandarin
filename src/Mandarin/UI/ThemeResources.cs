using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Mandarin.Interop;

namespace Mandarin.UI;

/// <summary>
/// Repaints the WPF Fluent theme with the interface palette and the accent color: every color and brush of the theme is
/// overridden in the application resources (the controls look them up dynamically), so buttons, fields, lists, menus,
/// check boxes and sliders follow the palette. Windows get the palette background and title bar instead of Mica.
/// </summary>
public static class ThemeResources
{
    static readonly List<object> Applied = new();

    // brushes drawn on top of an accent fill: text, check marks, switch knobs
    static readonly Regex OnAccentKey = new("OnAccent|AccentButtonForeground|CheckGlyph|RadioButtonCheckGlyphFill|ToggleButtonForegroundChecked|ToggleSwitchKnobFillOn|TodayForeground|RowSelectedForeground");

    public static event Action? Changed;

    public static void Apply(Application app)
    {
        var res = app.Resources;
        foreach (var k in Applied) res.Remove(k);
        Applied.Clear();
        void Set(object key, object value) { res[key] = value; Applied.Add(key); }

        // our accent replaces the system one unless it is turned off on the Windows palette
        var replaceAccent = Theme.ChosenAccent != null || Theme.Current != null;
        var ours = Variants(Theme.Accent);
        var sys = new[]
        {
            SystemColors.AccentColor, SystemColors.AccentColorLight1, SystemColors.AccentColorLight2, SystemColors.AccentColorLight3,
            SystemColors.AccentColorDark1, SystemColors.AccentColorDark2, SystemColors.AccentColorDark3
        };
        if (replaceAccent)
        {
            string[] names = { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" };
            ResourceKey[] colorKeys =
            {
                SystemColors.AccentColorKey, SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight3Key,
                SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark3Key
            };
            ResourceKey[] brushKeys =
            {
                SystemColors.AccentColorBrushKey, SystemColors.AccentColorLight1BrushKey, SystemColors.AccentColorLight2BrushKey, SystemColors.AccentColorLight3BrushKey,
                SystemColors.AccentColorDark1BrushKey, SystemColors.AccentColorDark2BrushKey, SystemColors.AccentColorDark3BrushKey
            };
            for (var i = 0; i < ours.Length; i++)
            {
                Set(names[i], ours[i]);
                Set(colorKeys[i], ours[i]);
                Set(brushKeys[i], Theme.Brush(ours[i]));
            }
        }

        var fluent = res.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true);
        if (fluent != null)
        {
            var onAccent = Theme.TextOn(Theme.IsDark ? ours[2] : ours[4]); // the theme fills with Light2 when dark, Dark1 when light
            foreach (var key in fluent.Keys.Cast<object>().ToList())
            {
                object? value;
                try { value = fluent[key]; } catch { continue; }
                var (c, brush) = value switch
                {
                    Color col => (col, false),
                    SolidColorBrush b => (b.Color, true),
                    _ => (default(Color?), false)
                };
                if (c is not { } color) continue;
                var mapped = color;
                var accentIndex = replaceAccent ? Array.FindIndex(sys, s => s.R == color.R && s.G == color.G && s.B == color.B && !(s.R == s.G && s.G == s.B)) : -1;
                if (accentIndex >= 0) mapped = Color.FromArgb(color.A, ours[accentIndex].R, ours[accentIndex].G, ours[accentIndex].B);
                else if (replaceAccent && IsBlackOrWhite(color) && key.ToString() is { } name && OnAccentKey.IsMatch(name) && !name.Contains("Disabled"))
                    mapped = Color.FromArgb(color.A, onAccent.R, onAccent.G, onAccent.B);
                else mapped = Theme.Map(color);
                if (mapped == color) continue;
                Set(key, brush ? Theme.Brush(mapped) : mapped);
            }
        }
        Changed?.Invoke();
    }

    static bool IsBlackOrWhite(Color c) => (c.R == 0 && c.G == 0 && c.B == 0) || (c.R == 255 && c.G == 255 && c.B == 255);

    /// <summary>Accent, Light1-3, Dark1-3 - the same steps Windows derives from its accent color.</summary>
    static Color[] Variants(Color a) => new[]
    {
        a, Theme.Mix(a, Colors.White, 0.12), Theme.Mix(a, Colors.White, 0.25), Theme.Mix(a, Colors.White, 0.40),
        Theme.Mix(a, Colors.Black, 0.10), Theme.Mix(a, Colors.Black, 0.24), Theme.Mix(a, Colors.Black, 0.40)
    };

    /// <summary>Title bar, corners and background of a regular window for the current palette.</summary>
    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (Theme.Current is { } p)
        {
            w.Background = Theme.Brush(p.Bg);
            static uint Rgb(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);
            if (hwnd != IntPtr.Zero) Native.ApplyWindowChrome(hwnd, p.Dark, false, (Rgb(p.Bg), Rgb(p.Text)));
        }
        else
        {
            w.ClearValue(Window.BackgroundProperty);
            if (hwnd != IntPtr.Zero) Native.ApplyWindowChrome(hwnd, Theme.IsDark);
        }
    }
}

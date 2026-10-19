using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Mandarin;

public enum AppLanguage { System, English, Russian }

/// <summary>
/// UI localization. Source strings are English and double as keys; translations live in
/// embedded JSON files (Localization/&lt;lang&gt;.json). A missing translation falls back to English.
/// </summary>
public static class L
{
    static Dictionary<string, string> _table = new();

    /// <summary>Two-letter code of the active UI language ("en", "ru").</summary>
    public static string Language { get; private set; } = "en";

    public static void Init(AppLanguage language)
    {
        Language = language switch
        {
            AppLanguage.English => "en",
            AppLanguage.Russian => "ru",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en"
        };
        _table = Language == "en" ? new() : Load(Language);
    }

    static Dictionary<string, string> Load(string lang)
    {
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Mandarin.Localization.{lang}.json");
            if (stream == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Could not load the {lang} translation", ex);
            return new();
        }
    }

    /// <summary>Translates an English UI string.</summary>
    public static string T(string english) =>
        _table.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    /// <summary>Translates an English composite format string and fills in the arguments.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>
    /// Count-dependent noun. English uses one/other; a translation may provide more forms in the
    /// key "one|other" → "form1|form2|form5" (Slavic rules: 1, 2–4, 5+).
    /// </summary>
    public static string Plural(int n, string one, string other)
    {
        if (_table.TryGetValue($"{one}|{other}", out var forms))
        {
            var f = forms.Split('|');
            if (f.Length == 3)
            {
                var m10 = n % 10; var m100 = n % 100;
                if (m10 == 1 && m100 != 11) return f[0];
                if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return f[1];
                return f[2];
            }
            if (f.Length == 2) return n == 1 ? f[0] : f[1];
        }
        return n == 1 ? one : other;
    }

    /// <summary>Native name of a language for the language picker.</summary>
    public static string NativeName(string twoLetter)
    {
        var name = CultureInfo.GetCultureInfo(twoLetter).NativeName;
        return name.Length == 0 ? twoLetter : char.ToUpper(name[0], CultureInfo.GetCultureInfo(twoLetter)) + name[1..];
    }
}

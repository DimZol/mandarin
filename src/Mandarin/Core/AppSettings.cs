using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mandarin.Core;

/// <summary>Key that is tapped while dragging to open the wheel.</summary>
public enum TriggerKey { None, Shift, Ctrl, Alt, Tab, Space, Backquote }

public enum OutputLocation { NextToOriginal, CustomFolder }

public enum PdfEpubMode { ReflowText, KeepPageLayout }

public enum AppTheme { System, Light, Dark }

/// <summary>What converts documents, spreadsheets and presentations.</summary>
public enum DocumentEngine
{
    /// <summary>Microsoft Office if installed; otherwise LibreOffice; otherwise the built-in converters.</summary>
    Auto,
    MicrosoftOffice,
    LibreOffice,
    BuiltIn
}

public sealed class AppSettings
{
    public TriggerKey ConvertKey { get; set; } = TriggerKey.Shift;
    public TriggerKey ToolsKey { get; set; } = TriggerKey.Ctrl;
    public TriggerKey ActionsKey { get; set; } = TriggerKey.Alt;
    public bool DragWheelEnabled { get; set; } = true;
    public int WheelSize { get; set; } = 320;

    public OutputLocation Output { get; set; } = OutputLocation.NextToOriginal;
    public string? CustomOutputFolder { get; set; }
    public bool RevealAfterConvert { get; set; }
    public bool PlaySound { get; set; } = true;

    public DocumentEngine DocumentEngine { get; set; } = DocumentEngine.Auto;
    public bool KeepMetadata { get; set; } = true;
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 88;
    public int AvifQuality { get; set; } = 65;
    public int HeicQuality { get; set; } = 75;
    public int PdfImageDpi { get; set; } = 300;
    public PdfEpubMode PdfToEpub { get; set; } = PdfEpubMode.ReflowText;
    public bool OcrScannedPdf { get; set; } = true;

    public int VideoCrf { get; set; } = 20;
    public string VideoPreset { get; set; } = "medium";
    public bool UseHardwareEncoder { get; set; }
    public string? FfmpegPath { get; set; }

    // start-with-Windows is opt-in: the registry is never written silently
    public bool StartWithWindows { get; set; }
    /// <summary>Theme of earlier versions; read once to choose the palette.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Interface palette: "windows", "auto" or a palette id (see Theme.Palettes).</summary>
    public string? Palette { get; set; }
    public bool AccentOn { get; set; } = true;
    public string AccentColor { get; set; } = "#F26B1D";
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public bool FirstRunDone { get; set; }

    public List<string> ShelfItems { get; set; } = new();

    [JsonIgnore]
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mandarin");

    [JsonIgnore]
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
            // settings of 1.1 and earlier: the light and dark themes become the matching palettes
            Current.Palette ??= Current.Theme switch { AppTheme.Light => "light", AppTheme.Dark => "gray", _ => "windows" };
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings", ex);
            Current = new();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
        Changed?.Invoke();
    }
}

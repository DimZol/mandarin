using Avalonia;
using Mandarin.Core;
using Mandarin.Platform;

namespace Mandarin;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // a file manager or the autostart may launch us with the user's folder as the working directory - leave it,
        // so that libraries and programs with matching names are never picked up from there (file arguments are made
        // absolute first: "mandarin photo.heic" from a terminal means the terminal's folder)
        args = args.Select(a => a.StartsWith("--") || !(File.Exists(a) || Directory.Exists(a)) ? a : Path.GetFullPath(a)).ToArray();
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        // the self-check runs with default settings and never saves them
        if (args.Length > 0 && args[0] == "--selftest")
        {
            App.SelfTest = args.Skip(1).ToArray();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        AppSettings.Load();
        L.Init(AppSettings.Current.Language);
        var langArg = args.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase));
        if (langArg != null && Enum.TryParse<AppLanguage>(langArg[7..], true, out var lang)) L.Init(lang);

        var single = new SingleInstance();
        if (!single.IsFirst)
        {
            SingleInstance.SendToFirst(args);
            single.Dispose();
            return 0;
        }
        App.Single = single;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            throw;
        }
        finally
        {
            single.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new X11PlatformOptions { WmClass = "Mandarin" });
}

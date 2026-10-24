using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Platform;
using Mandarin.UI;

namespace Mandarin;

/// <summary>Mandarin for Linux and Windows 7/8: tray, drag gesture, wheel, progress cards and the main window.</summary>
public sealed class App : Application
{
    public static SingleInstance? Single;
    public static WindowIcon? WindowIcon;
    public static Gesture? DragGesture;
    /// <summary>Arguments of "--selftest &lt;folder&gt; [shots|engines]".</summary>
    public static string[]? SelfTest;
    TrayIcon? _tray;
    NativeMenuItem? _gestureItem;

    public static new App Current => (App)Application.Current!;

    public override void Initialize()
    {
        Name = "Mandarin";
        using var icon = typeof(App).Assembly.GetManifestResourceStream("Mandarin.Assets.Mandarin.png");
        if (icon != null) WindowIcon = new WindowIcon(icon);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Startup(desktop.Args ?? Array.Empty<string>());
            desktop.Exit += (_, _) => OnExit();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void Startup(string[] args)
    {
        UiThread.CheckAccess = () => Dispatcher.UIThread.CheckAccess();
        UiThread.Post = a => Dispatcher.UIThread.Post(a);
        UiThread.Invoke = a =>
        {
            if (Dispatcher.UIThread.CheckAccess()) a();
            else Dispatcher.UIThread.Invoke(a);
        };
        if (SelfTest is { Length: > 0 } test)
        {
            L.Init(AppLanguage.English);
            Look.Apply(this);
            Dispatcher.UIThread.Post(async () =>
            {
                var code = 1;
                try { code = await SelfCheck.RunAsync(test[0], test.Length > 1 ? test[1] : "all"); }
                catch (Exception ex) { Console.WriteLine("FATAL: " + ex); }
                Console.Out.Flush();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown(code);
            });
            return;
        }

        Look.Apply(this);
        Motion.Init();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled error", e.Exception);
            e.Handled = true;
            try { Hud.ShowError(L.T("Error"), e.Exception is UserFacingException ? e.Exception.Message : JobManager.Describe(e.Exception)); }
            catch { /* HUD unavailable */ }
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task", ev.Exception); ev.SetObserved(); };
        Log.Info($"Starting Mandarin {typeof(App).Assembly.GetName().Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        Hud.Init();
        CreateTray();

        DragGesture = new Gesture { Enabled = AppSettings.Current.DragWheelEnabled };
        DragGesture.Triggered += (mode, x, y) => WheelWindow.ShowForDrag(mode, x, y);
        DragGesture.ButtonReleased += WheelWindow.OnGlobalButtonReleased;
        DragGesture.Escape += WheelWindow.CloseCurrent;

        if (Single != null)
        {
            Single.ArgumentsReceived += HandleArgs;
            Single.StartServer();
        }

        AppSettings.Changed += () =>
        {
            if (DragGesture != null) DragGesture.Enabled = AppSettings.Current.DragWheelEnabled;
            if (_gestureItem != null) _gestureItem.IsChecked = AppSettings.Current.DragWheelEnabled;
            Ffmpeg.ResetCache();
        };
        if (AppSettings.Current.StartWithWindows) Desktop.SetAutostart(true);

        // the system theme or accent changed: the System and Auto palettes follow it
        if (PlatformSettings != null)
            PlatformSettings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (Look.ChosenId is Look.WindowsId or Look.AutoId || !AppSettings.Current.AccentOn) ApplyTheme();
            });

        _ = Task.Run(OutputPaths.CleanupOldTemp);

        var background = args.Contains("--background");
        if (!AppSettings.Current.FirstRunDone)
        {
            AppSettings.Current.FirstRunDone = true;
            AppSettings.Save();
            HomeWindow.ShowSingle();
        }
        else if (!background && !args.Any(a => !a.StartsWith("--")))
        {
            HomeWindow.ShowSingle();
        }
        HandleArgs(args);
    }

    /// <summary>Applies the palette and the accent from settings and repaints the open windows.</summary>
    public static void ApplyTheme()
    {
        Look.Apply(Current);
        HomeWindow.Restyle();
        if (Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            foreach (var w in d.Windows.OfType<ToolWindow>()) w.Background = Look.WindowBackground;
    }

    void HandleArgs(string[] args)
    {
        if (args.Contains("--exit")) { ExitApp(); return; }
        var files = args.Where(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a))).Select(Path.GetFullPath).ToList();
        if (args.Contains("--show") && files.Count == 0) { HomeWindow.ShowSingle(); return; }
        if (files.Count > 0) WheelWindow.ShowForFiles(files);
    }

    void CreateTray()
    {
        try
        {
            var menu = new NativeMenu();
            NativeMenuItem Item(string text, Action click)
            {
                var item = new NativeMenuItem(text);
                item.Click += (_, _) => Dispatcher.UIThread.Post(click);
                menu.Items.Add(item);
                return item;
            }
            Item(L.T("Open Mandarin"), HomeWindow.ShowSingle);
            Item(L.T("Choose files to convert…"), () => _ = HomeWindow.PickFilesAndShowWheel());
            _gestureItem = new NativeMenuItem(L.T("Wheel while dragging"))
            {
#if AVALONIA12
                ToggleType = MenuItemToggleType.CheckBox,
#else
                ToggleType = NativeMenuItemToggleType.CheckBox,
#endif
                IsChecked = AppSettings.Current.DragWheelEnabled
            };
            _gestureItem.Click += (_, _) =>
            {
                AppSettings.Current.DragWheelEnabled = !AppSettings.Current.DragWheelEnabled;
                AppSettings.Save();
            };
            menu.Items.Add(_gestureItem);
            Item(L.T("Settings"), () => HomeWindow.ShowSingle("settings"));
            menu.Items.Add(new NativeMenuItemSeparator());
            Item(L.T("Exit"), ExitApp);
            _tray = new TrayIcon { Icon = WindowIcon, ToolTipText = L.T("Mandarin - file converter"), Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => HomeWindow.ShowSingle();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch (Exception ex) { Log.Error("Tray icon", ex); }
    }

    public async void ExitApp()
    {
        if (JobManager.Jobs.Any(j => j.IsRunning))
        {
            if (!await Ui.Ask(null, L.T("Some tasks are still running. Cancel them and exit?"))) return;
            foreach (var j in JobManager.Jobs.Where(j => j.IsRunning)) j.Cancel.Cancel();
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    void OnExit()
    {
        DragGesture?.Dispose();
        _tray?.Dispose();
        HtmlEngine.Shutdown();
    }
}

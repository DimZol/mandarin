using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mandarin.Core;
using Mandarin.Engines;
using Mandarin.Interop;
using Mandarin.UI;
using Forms = System.Windows.Forms;

namespace Mandarin;

public partial class App : Application
{
    SingleInstance? _single;
    DragGesture? _gesture;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _gestureItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // "Send to" and autostart launch us with the user's current folder as the working directory - leave it,
        // so that DLLs and programs with matching names are never picked up from there
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        AppSettings.Load();
        L.Init(AppSettings.Current.Language);
        // --lang=English|Russian overrides the interface language for this run only, settings stay unchanged
        var langArg = e.Args.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase));
        if (langArg != null && Enum.TryParse<AppLanguage>(langArg[7..], true, out var lang)) L.Init(lang);

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            SingleInstance.SendToFirst(e.Args);
            Shutdown();
            return;
        }

        ApplyThemeTo(this);
        _lookSignature = LookSignature();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Motion.Init();
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task", ev.Exception); ev.SetObserved(); };
        Log.Info($"Starting Mandarin {typeof(App).Assembly.GetName().Version}");

        Hud.Init();
        CreateTray();

        _gesture = new DragGesture(Dispatcher) { Enabled = AppSettings.Current.DragWheelEnabled };
        _gesture.Triggered += (mode, x, y) => WheelWindow.ShowForDrag(mode, x, y);
        _gesture.ButtonReleased += WheelWindow.OnGlobalButtonReleased;
        _gesture.Escape += WheelWindow.CloseCurrent;
        _gesture.Install();

        _single.ArgumentsReceived += HandleArgs;
        _single.StartServer();

        AppSettings.Changed += () =>
        {
            if (_gesture != null) _gesture.Enabled = AppSettings.Current.DragWheelEnabled;
            if (_gestureItem != null) _gestureItem.Checked = AppSettings.Current.DragWheelEnabled;
            Ffmpeg.ResetCache();
        };
        if (AppSettings.Current.StartWithWindows) Integration.SetAutostart(true);

        _ = Task.Run(OutputPaths.CleanupOldTemp);

        var background = e.Args.Contains("--background");
        if (!AppSettings.Current.FirstRunDone)
        {
            AppSettings.Current.FirstRunDone = true;
            AppSettings.Save();
            HomeWindow.ShowSingle();
        }
        else if (!background && !e.Args.Any(a => !a.StartsWith("--")))
        {
            HomeWindow.ShowSingle();
        }
        HandleArgs(e.Args);
        ShelfWindow.RestoreIfNeeded();
    }

    /// <summary>Applies the palette and the accent from settings and repaints the open windows.</summary>
    public static void ApplyTheme()
    {
        ApplyThemeTo(Current);
        _lookSignature = LookSignature();
        foreach (var w in Current.Windows.OfType<ToolWindow>()) ThemeResources.ApplyChrome(w);
        HomeWindow.Restyle();
        ShelfWindow.Restyle();
    }

    static string _lookSignature = "";

    /// <summary>What the look depends on besides the settings: the Windows light/dark mode and accent color.</summary>
    static string LookSignature() => $"{Theme.SystemIsDark()}|{SystemColors.AccentColor}|{AppSettings.Current.Palette}|{AppSettings.Current.AccentOn}|{AppSettings.Current.AccentColor}";

    /// <summary>The Windows theme or accent changed: the Windows and Auto palettes follow it.</summary>
    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
        // the Fluent theme switches its own colors first; repaint after it
        Dispatcher.BeginInvoke(() => { if (LookSignature() != _lookSignature) ApplyTheme(); }, DispatcherPriority.Background);
    }

    public static void ApplyThemeTo(Application app)
    {
        // the Windows palette follows the system; the others choose the light or dark Fluent base they repaint
        app.ThemeMode = Theme.Current is { } p ? p.Dark ? ThemeMode.Dark : ThemeMode.Light : ThemeMode.System;
        ThemeResources.Apply(app);
    }

    void HandleArgs(string[] args)
    {
        if (args.Contains("--exit")) { ExitApp(); return; }
        var files = args.Where(a => !a.StartsWith("--") && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (args.Contains("--show") && files.Count == 0) { HomeWindow.ShowSingle(); return; }
        if (args.Contains("--shelf")) { ShelfWindow.Toggle(); return; }
        if (files.Count > 0) WheelWindow.ShowForFiles(files);
    }

    void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Mandarin;component/Assets/Mandarin.ico"))?.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
            Text = L.T("Mandarin - file converter"),
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Open Mandarin"), null, (_, _) => Dispatcher.Invoke(HomeWindow.ShowSingle));
        menu.Items.Add(L.T("Choose files to convert…"), null, (_, _) => Dispatcher.Invoke(HomeWindow.PickFilesAndShowWheel));
        menu.Items.Add(L.T("Shelf"), null, (_, _) => Dispatcher.Invoke(ShelfWindow.Toggle));
        _gestureItem = new Forms.ToolStripMenuItem(L.T("Wheel while dragging")) { Checked = AppSettings.Current.DragWheelEnabled, CheckOnClick = true };
        _gestureItem.CheckedChanged += (_, _) =>
        {
            AppSettings.Current.DragWheelEnabled = _gestureItem.Checked;
            AppSettings.Save();
        };
        menu.Items.Add(_gestureItem);
        menu.Items.Add(L.T("Settings"), null, (_, _) => Dispatcher.Invoke(() => HomeWindow.ShowSingle("settings")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Exit"), null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(HomeWindow.ShowSingle); };
    }

    void ExitApp()
    {
        if (JobManager.Jobs.Any(j => j.IsRunning))
        {
            var r = MessageBox.Show(L.T("Some tasks are still running. Cancel them and exit?"), "Mandarin", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            foreach (var j in JobManager.Jobs.Where(j => j.IsRunning)) j.Cancel.Cancel();
        }
        Shutdown();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        try { Hud.ShowError(L.T("Error"), e.Exception is UserFacingException ? e.Exception.Message : JobManager.Describe(e.Exception)); }
        catch { /* HUD unavailable */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _gesture?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        HtmlEngine.Shutdown();
        _single?.Dispose();
        base.OnExit(e);
    }
}

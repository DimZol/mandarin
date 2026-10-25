using Mandarin.Core;
#if AVALONIA12
using Tmds.DBus.Protocol;
#endif

namespace Mandarin.Platform;

/// <summary>
/// KDE Plasma under Wayland: the pointer position. Wayland tells a program where the pointer is only over its own
/// windows (and Mandarin's windows go through XWayland, which knows it only over X11 windows), so a short KWin script
/// reads it and calls back over D-Bus - the way kdotool does it. Only the Linux build has it.
/// </summary>
public static class KWin
{
    /// <summary>A KDE Plasma session under Wayland.</summary>
    public static bool Available => OsInfo.IsLinux && X11.IsWayland &&
        ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "") + ":" + (Environment.GetEnvironmentVariable("KDE_FULL_SESSION") is { Length: > 0 } ? "KDE" : ""))
        .Split(':').Any(d => d.Equals("KDE", StringComparison.OrdinalIgnoreCase));

    /// <summary>The last position read, in X11 pixels (for the cases that cannot wait for KWin).</summary>
    public static (int x, int y)? LastCursor { get; private set; }

#if AVALONIA12
    const string ObjectPath = "/io/github/dimzol/Mandarin", Interface = "io.github.dimzol.Mandarin";
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DBusConnection? _bus;
    static TaskCompletionSource<string>? _reply;
    static string _token = "";
    static bool _broken;

    sealed class Callback : IPathMethodHandler
    {
        public string Path => ObjectPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.MemberAsString == "Cursor")
            {
                var text = request.GetBodyReader().ReadString();
                if (text.StartsWith(_token + " ", StringComparison.Ordinal)) _reply?.TrySetResult(text[(_token.Length + 1)..]);
                if (!context.NoReplyExpected)
                {
                    var w = context.CreateReplyWriter(null);
                    context.Reply(w.CreateMessage());
                }
            }
            else if (!context.NoReplyExpected) context.ReplyUnknownMethodError();
            return default;
        }
    }

    static async Task<DBusConnection> BusAsync()
    {
        if (_bus != null) return _bus;
        var bus = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus"));
        await bus.ConnectAsync();
        bus.AddMethodHandler(new Callback());
        return _bus = bus;
    }

    /// <summary>A method of KWin with string arguments.</summary>
    static async Task<T> CallAsync<T>(DBusConnection bus, string path, string iface, string member, string[] args, MessageValueReader<T> read)
    {
        MessageBuffer Create()
        {
            // MessageWriter is a struct: the body is written into this very variable, never into a copy
            var w = bus.GetMessageWriter();
            w.WriteMethodCallHeader(destination: "org.kde.KWin", path: path, @interface: iface, member: member,
                signature: args.Length > 0 ? new string('s', args.Length) : null);
            foreach (var a in args) w.WriteString(a);
            return w.CreateMessage();
        }
        return await bus.CallMethodAsync(Create(), read, null);
    }
#endif

    /// <summary>The pointer position in X11 pixels (what Avalonia windows and screens use); null when KWin does not tell.</summary>
    public static async Task<(int x, int y)?> CursorAsync()
    {
#if AVALONIA12
        if (!Available || _broken) return null;
        await Gate.WaitAsync();
        var script = "";
        string? plugin = null;
        try
        {
            var bus = await BusAsync();
            _token = Guid.NewGuid().ToString("N");
            plugin = "mandarin-cursor-" + _token[..8];
            _reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), plugin + ".js");
            await File.WriteAllTextAsync(script,
                "var p = workspace.cursorPos, g = workspace.virtualScreenGeometry;\n" +
                $"callDBus(\"{bus.UniqueName}\", \"{ObjectPath}\", \"{Interface}\", \"Cursor\", " +
                $"\"{_token} \" + p.x + \" \" + p.y + \" \" + g.x + \" \" + g.y + \" \" + g.width + \" \" + g.height);\n");

            var id = await CallAsync(bus, "/Scripting", "org.kde.kwin.Scripting", "loadScript", new[] { script, plugin },
                (m, _) => m.GetBodyReader().ReadInt32());
            if (id < 0) return null;
            // Plasma 6 names the script object /Scripting/ScriptN, Plasma 5 - /N
            try { await CallAsync(bus, $"/Scripting/Script{id}", "org.kde.kwin.Script", "run", Array.Empty<string>(), (_, _) => 0); }
            catch (DBusErrorReplyException) { await CallAsync(bus, $"/{id}", "org.kde.kwin.Script", "run", Array.Empty<string>(), (_, _) => 0); }

            var done = await Task.WhenAny(_reply.Task, Task.Delay(1500));
            if (done != _reply.Task) { Log.Info("KWin did not report the pointer position"); return null; }
            var v = _reply.Task.Result.Split(' ').Select(s => (int)Math.Round(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var pos = ToX11(v[0], v[1], v[2], v[3], v[4]);
            LastCursor = pos;
            return pos;
        }
        catch (Exception ex)
        {
            // no KWin scripting (an old or a locked-down Plasma): do not try on every wheel
            Log.Error("KWin pointer position", ex);
            if (ex is not TimeoutException) _broken = true;
            return null;
        }
        finally
        {
            if (plugin != null && _bus != null)
                try { await CallAsync(_bus, "/Scripting", "org.kde.kwin.Scripting", "unloadScript", new[] { plugin }, (m, _) => m.GetBodyReader().ReadBool()); }
                catch { /* already gone */ }
            if (script.Length > 0) try { File.Delete(script); } catch { /* temp */ }
            _reply = null;
            Gate.Release();
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    /// <summary>
    /// KWin speaks in logical pixels, XWayland in its own: the same, or multiplied by the scale when "legacy
    /// applications apply scaling themselves". The X11 root window covers the same desktop - its size gives the ratio.
    /// </summary>
    static (int x, int y) ToX11(int x, int y, int left, int top, int width)
    {
        var k = width > 0 && X11.RootSize() is { } root ? (double)root.width / width : 1;
        return ((int)Math.Round((x - left) * k), (int)Math.Round((y - top) * k));
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Mandarin.Core;
using Microsoft.Win32;

namespace Mandarin.Engines;

public enum OfficeApp { Word, Excel, PowerPoint }

public enum Backend { MicrosoftOffice, LibreOffice, BuiltIn }

/// <summary>
/// Microsoft Office via COM and LibreOffice in headless mode for accurate conversion of documents,
/// presentations and spreadsheets. The order is set in settings; in Auto mode Office comes before LibreOffice.
/// Macros are forcibly disabled when opening files.
/// </summary>
public static class OfficeEngine
{
    // whether Office apps are installed (regardless of the selected engine)
    public static bool WordInstalled => ProgIdExists("Word.Application");
    public static bool ExcelInstalled => ProgIdExists("Excel.Application");
    public static bool PowerPointInstalled => ProgIdExists("PowerPoint.Application");
    public static bool LibreInstalled => LibreOfficePath != null;

    /// <summary>Which engine handles this application's files with the current settings.</summary>
    public static Backend For(OfficeApp app)
    {
        var office = app switch
        {
            OfficeApp.Word => WordInstalled,
            OfficeApp.Excel => ExcelInstalled,
            _ => PowerPointInstalled
        };
        return AppSettings.Current.DocumentEngine switch
        {
            DocumentEngine.MicrosoftOffice => office ? Backend.MicrosoftOffice : Backend.BuiltIn,
            DocumentEngine.LibreOffice => LibreInstalled ? Backend.LibreOffice : Backend.BuiltIn,
            DocumentEngine.BuiltIn => Backend.BuiltIn,
            _ => office ? Backend.MicrosoftOffice : LibreInstalled ? Backend.LibreOffice : Backend.BuiltIn
        };
    }

    public static bool Native(OfficeApp app) => For(app) != Backend.BuiltIn;

    /// <summary>Explanation for errors when an external engine is required.</summary>
    public static string NeedEngineHint(string what) => AppSettings.Current.DocumentEngine == DocumentEngine.BuiltIn
        ? L.F("{0}: the \"Built-in only\" engine is selected in settings - switch it to Auto, Microsoft Office or LibreOffice", what)
        : L.F("{0}: Microsoft Office or LibreOffice is required (LibreOffice is free and can be installed from Mandarin settings)", what);

    static readonly Dictionary<string, bool> ProgIds = new();

    static bool ProgIdExists(string progId)
    {
        if (!OsInfo.IsWindows) return false; // Microsoft Office exists only on Windows
        lock (ProgIds)
        {
            if (!ProgIds.TryGetValue(progId, out var ok))
            {
                using var key = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
                ProgIds[progId] = ok = key != null;
            }
            return ok;
        }
    }

    public static string? LibreOfficePath
    {
        get
        {
            if (!OsInfo.IsWindows)
            {
                var linux = new List<string>
                {
                    "/usr/bin/soffice", "/usr/bin/libreoffice", "/usr/local/bin/soffice", "/usr/lib/libreoffice/program/soffice",
                    "/opt/libreoffice/program/soffice", "/snap/bin/libreoffice"
                };
                try { if (Directory.Exists("/opt")) linux.AddRange(Directory.GetDirectories("/opt", "libreoffice*").Select(d => Path.Combine(d, "program", "soffice"))); }
                catch { /* no access - skip */ }
                return linux.FirstOrDefault(File.Exists);
            }
            // the official installer writes the path to the registry; plus the standard folders
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var keyPath in new[] { @"SOFTWARE\LibreOffice\UNO\InstallPath", @"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath" })
                {
                    try
                    {
                        using var key = hive.OpenSubKey(keyPath);
                        if (key?.GetValue(null) is string dir && Path.IsPathFullyQualified(dir))
                        {
                            var exe = Path.Combine(dir, "soffice.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                    catch { /* no access to the key - try the next one */ }
                }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
    // Office apps dislike parallel automation - one at a time
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Runs Office automation on a separate STA thread. If the app hangs (for example on a hidden
    /// dialog) or the task is canceled, only the instances we started ourselves (/Automation) are closed.
    /// </summary>
    static async Task<T> StaAsync<T>(Func<T> work, CancellationToken ct, string processName, int timeoutSec = 180)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var before = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct).ContinueWith(_ => { }));
            if (finished != tcs.Task)
            {
                KillOwnInstances(processName, before);
                ct.ThrowIfCancellationRequested();
                throw new UserFacingException(L.F("{0} did not respond in time - the conversion was aborted", processName));
            }
            return await tcs.Task;
        }
        finally
        {
            Gate.Release();
        }
    }

    static void KillOwnInstances(string processName, HashSet<int> existedBefore)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            if (existedBefore.Contains(p.Id)) continue;
            try
            {
                // make sure it is an automation instance and not the user's window
                using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={p.Id}");
                var cmd = searcher.Get().Cast<System.Management.ManagementObject>().FirstOrDefault()?["CommandLine"] as string ?? "";
                if (cmd.Contains("/Automation", StringComparison.OrdinalIgnoreCase) || cmd.Contains("-Embedding", StringComparison.OrdinalIgnoreCase))
                    p.Kill(true);
            }
            catch (Exception ex) { Log.Error("Could not close the hung " + processName, ex); }
        }
    }

    static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new UserFacingException(L.F("{0} was not found", progId)))!;

    static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    // ---------- Word ----------

    public const int WdDocx = 16, WdPdf = 17, WdOdt = 23, WdRtf = 6, WdHtmlFiltered = 10, WdUnicodeText = 7;

    /// <summary>Opens a document in Word (including PDF) and saves it in the requested format.</summary>
    public static Task WordSaveAsAsync(string input, string output, int format, CancellationToken ct) => StaAsync(() =>
    {
        dynamic? app = null, doc = null;
        try
        {
            app = Create("Word.Application");
            app.Visible = false;
            app.DisplayAlerts = 0;
            app.AutomationSecurity = 3;
            // a wrong "password" instead of a dialog: a protected document fails right away instead of hanging Word
            // (the user's Word/Excel settings are not changed here - only open parameters)
            doc = app.Documents.Open(FileName: Path.GetFullPath(input), ConfirmConversions: false, ReadOnly: true,
                AddToRecentFiles: false, Visible: false, OpenAndRepair: false, NoEncodingDialog: true,
                PasswordDocument: "\u0001", WritePasswordDocument: "\u0001");
            if (format == WdPdf)
                doc.ExportAsFixedFormat(OutputFileName: Path.GetFullPath(output), ExportFormat: 17, OpenAfterExport: false,
                    OptimizeFor: 0, CreateBookmarks: 1, DocStructureTags: true);
            else if (format == WdUnicodeText)
                doc.SaveAs2(FileName: Path.GetFullPath(output), FileFormat: format, Encoding: 65001, AddToRecentFiles: false);
            else
                doc.SaveAs2(FileName: Path.GetFullPath(output), FileFormat: format, AddToRecentFiles: false);
            return true;
        }
        catch (COMException ex)
        {
            throw new UserFacingException(L.T("Microsoft Word could not process the file: ") + ex.Message);
        }
        finally
        {
            try { doc?.Close(SaveChanges: 0); } catch { }
            try { app?.Quit(SaveChanges: 0); } catch { }
            Release(doc); Release(app);
        }
    }, ct, "WINWORD");

    // ---------- Excel ----------

    public const int XlPdf = -1, XlXlsx = 51, XlOds = 60, XlCsvUtf8 = 62, XlUnicodeText = 42;

    public static Task ExcelSaveAsAsync(string input, string output, int format, CancellationToken ct) => StaAsync(() =>
    {
        dynamic? app = null, wb = null;
        try
        {
            app = Create("Excel.Application");
            app.Visible = false;
            app.DisplayAlerts = false;
            app.AutomationSecurity = 3;
            wb = app.Workbooks.Open(Filename: Path.GetFullPath(input), UpdateLinks: 0, ReadOnly: true, AddToMru: false,
                Password: "\u0001", WriteResPassword: "\u0001", IgnoreReadOnlyRecommended: true, Notify: false);
            if (format == XlPdf)
                wb.ExportAsFixedFormat(Type: 0, Filename: Path.GetFullPath(output), Quality: 0, IncludeDocProperties: true,
                    IgnorePrintAreas: false, OpenAfterPublish: false);
            else
                wb.SaveAs(Filename: Path.GetFullPath(output), FileFormat: format, AddToMru: false, Local: true);
            return true;
        }
        catch (COMException ex)
        {
            throw new UserFacingException(L.T("Microsoft Excel could not process the file: ") + ex.Message);
        }
        finally
        {
            try { wb?.Close(SaveChanges: false); } catch { }
            try { app?.Quit(); } catch { }
            Release(wb); Release(app);
        }
    }, ct, "EXCEL");

    // ---------- PowerPoint ----------

    public const int PpPdf = 32, PpPptx = 24;

    public static Task PowerPointSaveAsAsync(string input, string output, int format, CancellationToken ct) => StaAsync(() =>
    {
        dynamic? app = null, pres = null;
        try
        {
            app = Create("PowerPoint.Application");
            app.DisplayAlerts = 1; // ppAlertsNone
            app.AutomationSecurity = 3;
            pres = app.Presentations.Open(FileName: Path.GetFullPath(input), ReadOnly: -1, Untitled: 0, WithWindow: 0);
            if (format == PpPdf)
                pres.SaveAs(FileName: Path.GetFullPath(output), FileFormat: PpPdf, EmbedTrueTypeFonts: 0);
            else
                pres.SaveAs(FileName: Path.GetFullPath(output), FileFormat: format);
            return true;
        }
        catch (COMException ex)
        {
            throw new UserFacingException(L.T("Microsoft PowerPoint could not process the file: ") + ex.Message);
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { app?.Quit(); } catch { }
            Release(pres); Release(app);
        }
    }, ct, "POWERPNT");

    /// <summary>Exports every slide to an image; returns the paths.</summary>
    public static Task<List<string>> PowerPointExportSlidesAsync(string input, string folder, string ext, int longSidePx, CancellationToken ct) => StaAsync(() =>
    {
        dynamic? app = null, pres = null;
        var files = new List<string>();
        try
        {
            app = Create("PowerPoint.Application");
            app.DisplayAlerts = 1;
            app.AutomationSecurity = 3;
            pres = app.Presentations.Open(FileName: Path.GetFullPath(input), ReadOnly: -1, Untitled: 0, WithWindow: 0);
            double sw = pres.PageSetup.SlideWidth, sh = pres.PageSetup.SlideHeight;
            int w, h;
            if (sw >= sh) { w = longSidePx; h = (int)Math.Round(longSidePx * sh / sw); }
            else { h = longSidePx; w = (int)Math.Round(longSidePx * sw / sh); }
            int count = pres.Slides.Count;
            for (var i = 1; i <= count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(folder, $"{FileKinds.BaseName(input)} — {i:000}.{ext}");
                dynamic slide = pres.Slides[i];
                slide.Export(file, ext == "png" ? "PNG" : "JPG", w, h);
                Release(slide);
                files.Add(file);
            }
            return files;
        }
        catch (COMException ex)
        {
            throw new UserFacingException(L.T("Microsoft PowerPoint could not export the slides: ") + ex.Message);
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { app?.Quit(); } catch { }
            Release(pres); Release(app);
        }
    }, ct, "POWERPNT", 600);

    // ---------- LibreOffice ----------

    // one LibreOffice profile cannot be used by several processes at once
    static readonly SemaphoreSlim LibreGate = new(1, 1);

    /// <summary>
    /// soffice --headless --convert-to in Mandarin's own profile (the user's LibreOffice settings
    /// are not touched). The result is moved to <paramref name="output"/>; an existing file is never overwritten.
    /// </summary>
    public static async Task LibreSaveAsAsync(string input, string output, string filter, CancellationToken ct, int timeoutSec = 300)
    {
        var soffice = LibreOfficePath ?? throw new UserFacingException(L.T("LibreOffice was not found"));
        var outDir = OutputPaths.TempDir();
        await LibreGate.WaitAsync(ct);
        try
        {
            var profile = new Uri(Path.Combine(AppSettings.Folder, "lo-profile")).AbsoluteUri;
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = outDir
            };
            foreach (var a in new[] { $"-env:UserInstallation={profile}", "--headless", "--norestore", "--nolockcheck", "--nodefault",
                         "--convert-to", filter, "--outdir", outDir, Path.GetFullPath(input) })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            // read the output, otherwise the process hangs once the pipe buffer is full
            var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* already exited */ }
                ct.ThrowIfCancellationRequested();
                throw new UserFacingException(L.T("LibreOffice did not respond in time - the conversion was aborted"));
            }
            var result = Directory.GetFiles(outDir).FirstOrDefault();
            if (result == null)
            {
                var log = (await stderr + "\n" + await stdout).Trim();
                Log.Error("LibreOffice: " + log);
                throw new UserFacingException(L.T("LibreOffice could not convert the file"));
            }
            File.Move(result, output, overwrite: false);
        }
        finally
        {
            LibreGate.Release();
            OutputPaths.TryDelete(outDir);
        }
    }

    // LibreOffice export filters
    public static string LibreFilter(string target, OfficeApp app) => (target, app) switch
    {
        ("pdf", OfficeApp.Word) => "pdf:writer_pdf_Export",
        ("pdf", OfficeApp.Excel) => "pdf:calc_pdf_Export",
        ("pdf", OfficeApp.PowerPoint) => "pdf:impress_pdf_Export",
        ("docx", _) => "docx:MS Word 2007 XML",
        ("odt", _) => "odt:writer8",
        ("rtf", _) => "rtf:Rich Text Format",
        ("xlsx", _) => "xlsx:Calc MS Excel 2007 XML",
        ("ods", _) => "ods:calc8",
        ("pptx", _) => "pptx:Impress MS PowerPoint 2007 XML",
        _ => target
    };

    /// <summary>
    /// Conversion with the selected native engine (Office or LibreOffice).
    /// false if no suitable engine exists in settings/system and the built-in path is needed.
    /// </summary>
    public static async Task<bool> TrySaveAsAsync(OfficeApp app, string input, string output, string target, Action<string>? detail, CancellationToken ct)
    {
        switch (For(app))
        {
            case Backend.MicrosoftOffice:
                detail?.Invoke(app switch { OfficeApp.Word => "Microsoft Word…", OfficeApp.Excel => "Microsoft Excel…", _ => "Microsoft PowerPoint…" });
                switch (app)
                {
                    case OfficeApp.Word:
                        var wd = target switch { "pdf" => WdPdf, "docx" => WdDocx, "odt" => WdOdt, "rtf" => WdRtf, _ => -1 };
                        if (wd < 0) return false;
                        await WordSaveAsAsync(input, output, wd, ct);
                        return true;
                    case OfficeApp.Excel:
                        var xl = target switch { "pdf" => XlPdf, "xlsx" => XlXlsx, "ods" => XlOds, _ => 0 };
                        if (xl == 0) return false;
                        await ExcelSaveAsAsync(input, output, xl, ct);
                        return true;
                    default:
                        var pp = target switch { "pdf" => PpPdf, "pptx" => PpPptx, _ => 0 };
                        if (pp == 0) return false;
                        await PowerPointSaveAsAsync(input, output, pp, ct);
                        return true;
                }
            case Backend.LibreOffice:
                detail?.Invoke("LibreOffice…");
                await LibreSaveAsAsync(input, output, LibreFilter(target, app), ct);
                return true;
            default:
                return false;
        }
    }
}
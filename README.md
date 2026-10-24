<p align="center">
  <b>English</b> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="docs/logo.png" width="128" alt="Mandarin logo">
</p>

<h1 align="center">Mandarin</h1>

<p align="center">
  The zero-click offline file converter for Windows 11 – also for Linux and Windows 7.<br>
  Start dragging a file, tap a key, drop it on a format. Done.
</p>

<p align="center">
  <img src="docs/screenshots/convert.gif" width="760" alt="Dragging a photo in File Explorer, tapping Shift and dropping it on PNG">
</p>

## How it works

Start dragging any file or folder in File Explorer (or any app that drags files), then **tap a key** while
still holding the mouse button. A wheel opens under the cursor. Drop the file on the option you need and
the result appears next to the original. The original is never modified.

| Key while dragging | Wheel |
|---|---|
| **Shift** | Convert to another format |
| **Ctrl** | Tools: compress, crop, trim, metadata, split, join, redact, background… |
| **Alt** | Actions: share, shelf, copy, move, copy path, terminal, ZIP |

<p align="center">
  <img src="docs/screenshots/wheel-convert.png" width="270" alt="Convert wheel">
  <img src="docs/screenshots/wheel-tools.png" width="270" alt="Tools wheel">
  <img src="docs/screenshots/wheel-actions.png" width="270" alt="Actions wheel">
</p>

Keys can be changed in settings. Release the file in the center of the wheel (or press Esc) to cancel.

The interface is animated throughout: the wheel unfolds sector by sector, the option under the cursor lights up
and moves outward, progress cards slide in and fold away, and pages, cards and buttons respond to the mouse.
Turning off *Animation effects* in Windows settings turns the animations off in Mandarin too.

### Appearance

**Settings → Appearance** has the interface palettes of LaserPrep – *Windows* (the system colors with Mica, the
default), *Auto* (follows the Windows light or dark mode), *Gray*, *Graphite*, *Light gray*, *Steel*, *Teal*,
*Lilac*, *Amber*, *Coffee* and *Burgundy* – and the accent color: Mandarin's tangerine, ten more presets or any
custom color. The accent can also be turned off to keep the palette's own neutral colors. Everything is repainted at
once: the windows, the wheel, the progress cards and the title bars.

<p align="center">
  <img src="docs/screenshots/palettes.png" width="720" alt="Palettes and accent colors in the settings">
</p>

No gesture needed if you prefer clicks: drop files on the Mandarin window, use **Choose files…** in the
tray menu, or enable **Send to → Mandarin** in the Explorer context menu.

## Features

### Conversion

| Type | From | To |
|---|---|---|
| Images | JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, GIF, SVG, ICO, PSD, RAW | JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, PDF, ICO; GIF → MP4/WebM |
| Video | MP4, MOV, MKV, WebM, AVI, WMV, M4V, FLV, MPG, 3GP, TS | MP4, MOV, MKV, WebM, AVI, WMV, GIF, MP3 |
| Audio | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA, AAC | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA |
| PDF | PDF | DOCX, PPTX, XLSX, JPG, PNG, EPUB, TXT, Markdown, HTML (scans via Windows OCR) |
| Documents | DOCX, DOC, ODT, RTF, Markdown, TXT, HTML | DOCX, ODT, PDF, TXT, Markdown, HTML, EPUB, PNG, JPG |
| Presentations | PPTX, PPT, ODP | PDF, JPG, PNG, PPTX |
| Spreadsheets | XLSX, XLS, ODS, CSV, TSV | XLSX, ODS, CSV, TSV, PDF |
| EPUB | EPUB | PDF, DOCX, ODT, TXT, Markdown, HTML |
| Subtitles | SRT, VTT | SRT, VTT, TXT |
| Archives and folders | ZIP, TAR, GZIP, RAR, 7z, BZ2, XZ, folders | ZIP, TAR, GZIP |

### Tools

- **Images** – compress (preset, percentage or exact file size, optional resize), EXIF and GPS metadata,
  photo editor (exposure, contrast, highlights/shadows, color, sharpness, vignette, filters), annotations
  (arrows, shapes, pen, marker, text, numbered callouts), background (color, gradient, image or blur with
  aspect ratio, padding, corner radius and shadow), crop (aspect presets, exact size, rotate, flip),
  redaction of faces and text (detected locally by Windows), rotate, combine into PDF, collage.
- **Video** – compress (including to a target size), metadata, mute, frame-accurate trim, crop, speed
  0.25–4× with pitch preserved, save frames as PNG, split into parts, time-ranged area redaction, join.
- **Audio** – compress, tags, loudness normalization (LUFS), waveform video, trim with silence removal,
  mono/stereo, bleep marked fragments, join.
- **PDF** – compress (4 levels), properties, split, reorder/rotate/duplicate/delete pages, merge.
- **Archives** – extract with protection against path traversal and archive bombs.

### Actions

Windows Share sheet (Nearby sharing – the AirDrop counterpart), floating shelf, copy to clipboard,
move to a folder, copy path, open in Terminal, pack into ZIP.

<p align="center">
  <img src="docs/screenshots/home.png" width="720" alt="Main window">
</p>

<p align="center">
  <img src="docs/screenshots/photo-editor.png" width="720" alt="Photo editor">
</p>

<p align="center">
  <img src="docs/screenshots/video-trim.png" width="720" alt="Video trimming">
</p>

## Privacy and safety

- Everything runs locally. Images go through ImageMagick, video and audio through FFmpeg, PDFs through
  built-in Windows features, PdfPig and PDFsharp, text and face recognition through Windows. Nothing is
  uploaded anywhere; printing HTML to PDF blocks every network request.
- Originals are never modified or overwritten: results always get a free name
  (`photo.png`, `photo (1).png`, `photo (compressed).jpg`…). FFmpeg runs with `-n`, archives are created
  with `CreateNew`, and on failure only files created by the task itself are removed.
- Dropping a file on the wheel is always a copy, never a move.
- Extraction never leaves the new folder, never creates links or devices from TAR files and is limited
  by the free disk space minus 1 GB.
- HTML and Markdown inline only images from the document's own folder; absolute paths and `file://`
  are blocked and remote images are never downloaded. XML from EPUB/ODT/ODS is read without DTDs.
- Office documents are opened with macros disabled and without changing your Office settings; hung
  Office instances started by Mandarin are closed after a timeout.
- The global input hooks only observe the mouse and the trigger keys: nothing is swallowed, recorded
  or logged.

## Linux and Windows 7/8

There is a separate build of Mandarin for **Linux** (x64) and for **Windows 7 SP1 / 8 / 8.1** (x64). It has the same
conversions, tools and actions, the wheel, the progress cards, the palettes and the tray icon, on an Avalonia
interface. Both builds are self-contained: no .NET needs to be installed.

<p align="center">
  <img src="docs/screenshots/linux.png" width="720" alt="Mandarin on Linux">
</p>

How it differs from the Windows 10/11 app:

- **The drag gesture** works on Windows 7/8 and in X11 sessions on Linux. Under Wayland the system does not let
  programs see keys pressed in other windows, so open files with **Open with → Mandarin** in the file manager
  (turn the entry on in **Settings → Desktop integration**), drop them on the Mandarin window or pick them from the
  tray menu. In the wheel the same keys switch the modes.
- **PDF pages** are rendered with the built-in PDFium. **Text recognition** in scans and screenshots uses
  [Tesseract](https://github.com/tesseract-ocr/tesseract) when it is installed; face detection is only in the
  Windows 10/11 app (areas are marked by hand).
- **PDF from documents** is printed by LibreOffice or a Chromium-based browser (Chromium, Chrome, Edge, Brave,
  Vivaldi) in headless mode with the network switched off.
- **Video and audio preview** in the trim, split, speed and crop tools shows frames decoded by FFmpeg; the sound
  plays through `ffplay` when it is installed next to FFmpeg.
- There is no Share sheet and no shelf; actions are copy, move, copy path, terminal, show in folder and ZIP.
- Settings live in `~/.config/Mandarin` on Linux and in `%APPDATA%\Mandarin` on Windows.

**Linux:** download `Mandarin-<version>-linux-x64.tar.gz`, unpack it anywhere and run `./Mandarin`. It needs
X11 libraries (`libx11-6 libice6 libsm6 libfontconfig1`, present on every desktop), and optionally:

```bash
sudo apt install ffmpeg tesseract-ocr tesseract-ocr-rus libreoffice
```

On a desktop without a compositor the wheel is drawn on a plain rectangle instead of floating over the screen.

**Windows 7/8:** download `Mandarin-<version>-win7-x64.zip`, unpack it and run `Mandarin.exe`. Windows 7 needs
SP1 with the *Universal C Runtime* update (KB2999226, part of the regular updates); it is built on .NET 6, the
last .NET that supports Windows 7. LibreOffice 24.8 is the last version for Windows 7 and 8. This build has been
tested on Windows 11 and Linux, not on a real Windows 7 computer.

## Requirements

- Windows 10 2004+ or Windows 11, x64 (for Linux and Windows 7/8 see above).
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for the
  self-contained build).
- **FFmpeg** for video and audio: `winget install Gyan.FFmpeg`. Mandarin finds it automatically or you
  can set the path in settings.
- **Documents, spreadsheets and presentations** – the engine is chosen in settings:
  - **Auto** (default): Microsoft Office if installed → otherwise LibreOffice → otherwise built-in converters;
  - **Microsoft Office** or **LibreOffice** only (falls back to built-in if not installed);
  - **Built-in only**: no external programs. Text, headings, lists, tables and images are kept,
    but not the exact page layout; presentations and legacy DOC files are unavailable.

  LibreOffice is free and can be installed from Mandarin settings (`winget`, about 1.5 GB). Mandarin runs
  it with its own profile and never touches your LibreOffice settings.
- Saving HEIC needs the *HEIF Image Extensions* and *HEVC Video Extensions* from the Microsoft Store.

## Languages

The interface is available in English and Russian. By default Mandarin follows the Windows display
language; it can be changed in **Settings → Windows integration → Language**. Translations live in
`src/Mandarin/Localization/<language>.json` (English strings are the keys), so adding a language is a
matter of adding one JSON file.

## Installation

1. Download `Mandarin-<version>-win-x64-framework-dependent.zip` from the
   [Releases](../../releases) page.
2. Extract it anywhere (for example `%LOCALAPPDATA%\Programs\Mandarin`) and run `Mandarin.exe`.
3. Optionally enable **Start Mandarin with Windows** on the home screen so the wheel is always available.

Command line: `Mandarin.exe <files>` opens the wheel for the files, `--background` starts in the tray only,
`--show` opens the main window, `--shelf` toggles the shelf, `--exit` closes the running instance and
`--lang=English` (or `Russian`) sets the interface language for that run without changing the settings.

## Building from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # dist\Mandarin
powershell -ExecutionPolicy Bypass -File build.ps1 -Zip     # plus a release archive
powershell -ExecutionPolicy Bypass -File build.ps1 -Linux -Zip   # dist\Mandarin-linux + .tar.gz
powershell -ExecutionPolicy Bypass -File build.ps1 -Win7 -Zip    # dist\Mandarin-win7 + .zip
```

The Linux and Windows 7 build checks itself: `Mandarin --selftest <empty folder>` converts generated files with
every engine available on the computer and renders every window to PNG.

Engine self-test (creates its test files only inside the given folder):

```powershell
dotnet run --project tests\Mandarin.SelfTest -- C:\Temp\mandarin-test
```

The same tool renders the screenshots off screen (`-- <folder> ui`), records the animations off screen as
frames and checks smooth scrolling (`-- <folder> motion`), and records the demo GIF frames with a running
Mandarin instance (`-- <folder> record`; it moves the mouse for about 15 seconds).

## Project layout

```
src/Mandarin
  Core/          settings, file classification, format matrix, job queue, output naming, localization
  Engines/       ImageEngine (Magick.NET), MediaEngine + Ffmpeg, PdfEngine (PdfPig, PDFsharp, Windows.Data.Pdf),
                 DocumentConverter, DocxReader, HtmlEngine (WebView2), EpubEngine, PptxWriter, SheetEngine,
                 SubtitleEngine, ArchiveEngine, OfficeEngine (COM / LibreOffice), WinRt (OCR, faces, HEIC)
  Interop/       global drag gesture (low-level hooks), shell thumbnails, Share sheet,
                 single instance, autostart, "Send to"
  UI/            wheel, progress cards, main window, shelf, tool windows
  Localization/  UI translations
src/Mandarin.Cross        Linux and Windows 7/8 build: Avalonia interface, the same engines (linked from src/Mandarin),
                          Platform/ (PDFium, Tesseract, headless printing, X11 and Windows drag gesture, desktop integration)
tests/Mandarin.SelfTest   runs every conversion and tool on generated files
```

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The photo in the screenshots is
[Citrus reticulata fruit 2009 G1.jpg](https://commons.wikimedia.org/wiki/File:Citrus_reticulata_fruit_2009_G1.jpg)
by George Chernilevsky, public domain, via Wikimedia Commons.

# Third-party notices

Mandarin is distributed under the MIT License (see `LICENSE`). It uses the following components,
each under its own license. Their license texts are available at the linked project pages and in
the NuGet packages.

| Component | License | Project |
|---|---|---|
| Magick.NET (ImageMagick and its bundled native libraries) | Apache-2.0 (ImageMagick License for ImageMagick) | https://github.com/dlemstra/Magick.NET |
| PdfPig | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| PDFsharp 6.2 | MIT | https://github.com/empira/PDFsharp |
| DocumentFormat.OpenXml (Open XML SDK) | MIT | https://github.com/dotnet/Open-XML-SDK |
| ClosedXML | MIT | https://github.com/ClosedXML/ClosedXML |
| ExcelDataReader | MIT | https://github.com/ExcelDataReader/ExcelDataReader |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| ReverseMarkdown | MIT | https://github.com/mysticmind/reversemarkdown-net |
| HtmlAgilityPack | MIT | https://github.com/zzzprojects/html-agility-pack |
| HtmlToOpenXml | MIT | https://github.com/onizet/html2openxml |
| SharpCompress | MIT | https://github.com/adamhathcock/sharpcompress |
| Microsoft.Web.WebView2 | Microsoft WebView2 SDK license (BSD-style) | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| System.Management | MIT | https://github.com/dotnet/runtime |

The Linux and Windows 7/8 build additionally uses:

| Component | License | Project |
|---|---|---|
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp and HarfBuzzSharp (Skia, HarfBuzz) | MIT (BSD-3-Clause for Skia, MIT for HarfBuzz) | https://github.com/mono/SkiaSharp |
| Inter font (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| Material.Icons (Material Design Icons by Pictogrammers) | MIT (icons: Apache-2.0) | https://github.com/SKProCH/Material.Icons |
| Docnet.Core with PDFium | MIT (PDFium: BSD-3-Clause / Apache-2.0) | https://github.com/GowenGit/docnet |
| .NET 6 runtime (Windows 7/8 build) | MIT | https://github.com/dotnet/runtime |

Not bundled, used only if installed on the computer:

- **FFmpeg** (LGPL/GPL, depending on the build) - video and audio. Install it with
  `winget install Gyan.FFmpeg` or point Mandarin to an existing `ffmpeg.exe`.
- **Microsoft Office** - optional engine for documents, spreadsheets and presentations.
- **LibreOffice** (MPL-2.0) - optional free engine for documents, spreadsheets and presentations.
- **Microsoft Edge WebView2 Runtime** - part of Windows 11, used to print HTML to PDF.
- **Chromium, Google Chrome, Microsoft Edge, Brave or Vivaldi** - on Linux and Windows 7/8, print HTML to PDF
  in headless mode with the network switched off.
- **Tesseract OCR** (Apache-2.0) - on Linux and Windows 7/8, text recognition in scans and screenshots.

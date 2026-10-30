# PageSmith

PageSmith is a native Windows 11 PDF reader and toolbox. It covers every tool of the iLovePDF
web app (Free and Premium tiers) and runs on your PC without an internet connection.
Features that need the internet (web page capture, translation) check the connection first
and offer *Retry* when you are offline.

- **Read like in Adobe Acrobat:** PDFs open in tabs, with thumbnails, bookmarks, links, find,
  text selection, highlights and notes, print and document properties. Images, Office files and
  web pages open as new PDFs.
- **Every tool works on the open document:** merge, compress, OCR, rotate, edit, sign, protect,
  redact and the rest replace the document in its tab, with undo, until you save. Conversions,
  split and compare save new files next to it.
- **Opening PDFs from Explorer:** *Settings → Opening PDFs* adds PageSmith to *Open with*, and
  PDFs opened while it runs join the same window as tabs.

- [TODO.md](TODO.md): what works today, what's next (v1.0 and the machine-learning v2.0), and
  a detailed build guide.
- [docs/FEATURES.md](docs/FEATURES.md): the iLovePDF feature analysis and how each feature
  maps to PageSmith.

## Requirements

- Windows 10 2004 or later (Windows 11 recommended for Mica and the Fluent look)
- .NET 10 SDK to build

## Quick start

```bash
dotnet build PageSmith.slnx
```

```bash
dotnet run --project src/PageSmith.App
```

```bash
dotnet test tests/PageSmith.Tests
```

To build an installer (`artifacts/installer/PageSmith-Setup-<version>-win-x64.exe`) for other PCs,
with [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed:

```bash
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

Or, to build a standalone `PageSmith.exe` that runs without .NET installed:

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Zip
```

See [TODO.md §5](TODO.md#5-how-to-build-pagesmith) for prerequisites, optional components
(Office, OCR languages, WebView2), publishing options and developer notes.

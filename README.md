# PageSmith

PageSmith is a native Windows 11 PDF toolbox that covers every tool of the iLovePDF web
app (Free and Premium tiers) and runs on your PC without an internet connection.
Features that need the internet (web page capture, translation) check the connection first
and offer *Retry* when you are offline.

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

To build a standalone `PageSmith.exe` that runs without .NET installed:

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Zip
```

See [TODO.md §5](TODO.md#5-how-to-build-pagesmith) for prerequisites, optional components
(Office, OCR languages, WebView2), publishing options and developer notes.

# PageSmith — status, roadmap and build guide

PageSmith is a native Windows 11 PDF toolbox that recreates every tool of the iLovePDF web app,
Free and Premium tiers, as an offline desktop app. This file tracks:

- what works today;
- what is left for v1.0 and the machine-learning v2.0;
- how to build, test and package the app.

For the iLovePDF research behind the feature list (plans, every tool's options, the Free vs
Premium limits), see [docs/FEATURES.md](docs/FEATURES.md).

**Current version:** 0.2.0 (preview), last updated 2026-10-26.

---

## 1. Where things stand

| Area | State |
|---|---|
| iLovePDF analysis | Done: 34 features (33 tools + Workflows), Free vs Premium limits, mapping → [docs/FEATURES.md](docs/FEATURES.md) |
| PDF engine (`PageSmith.Core`) | Done for all 33 tools; 82 automated tests, all passing |
| Windows app (`PageSmith.App`) | **All 33 tool screens working**, plus Home, Workflows, Settings and About |
| Still to check by hand | A real scanner, a real camera, and *Retry* with the PC actually offline (§2.1) |
| Machine-learning features | Planned for v2.0 (§4) |
| Packaging | Self-contained single-file `.exe` and zip via `scripts/publish.ps1`; no installer yet |

How the tools were checked:

- **UI run** means the tool was driven through the real app window: files loaded, items placed
  on the page where the tool needs them, the button pressed, and the output file opened and
  looked at.
- **Unit tests** means the engine is covered by `tests/PageSmith.Tests`.
- Page-editor tools were driven with the `--demo` switch, which places items through the same
  code paths as mouse clicks (see §5.6). Mouse dragging itself was not automated.

### 1.1 Tools

| # | Tool | Network | iLovePDF tier → PageSmith | Verified by |
|---|---|---|---|---|
| 1 | Merge PDF | Offline | Free (25 files) → unlimited; bookmarks per file, per-file rotation, sort | Unit tests, screen check |
| 2 | Split PDF | Offline | Custom/fixed ranges, extract all/select, **split by size** and **smart split** (Premium) included | Unit tests, UI run |
| 3 | Remove pages | Offline | Free | Unit tests |
| 4 | Extract pages | Offline | Free | Unit tests |
| 5 | Organize PDF | Offline | Reorder, rotate, delete, duplicate, insert blank, add more PDFs | Unit tests, screen check |
| 6 | Scan to PDF | Offline | Scanner (Windows scan dialog), camera with live preview, or photos; colour, greyscale or black & white clean-up; page size, orientation, margin | Unit tests (filters), UI run with a photo |
| 7 | Compress PDF | Offline | Extreme / Recommended / Less; never makes a file bigger | Unit tests, UI run |
| 8 | Repair PDF | Offline | Three-stage recovery (rebuild → lenient parse → re-render) | Unit tests |
| 9 | OCR PDF | Offline | Free 50 pages → unlimited; any OCR language installed in Windows | Unit tests |
| 10 | JPG to PDF | Offline | JPG/PNG/BMP/GIF/TIFF, orientation, page size, margins, EXIF rotation | Unit tests, UI run |
| 11 | Word to PDF | Offline | Microsoft Office → LibreOffice → built-in converter | Unit tests |
| 12 | PowerPoint to PDF | Offline | Same engines as Word to PDF | Unit tests |
| 13 | Excel to PDF | Offline | Same engines as Word to PDF | Unit tests |
| 14 | HTML to PDF | Online for web addresses | Screen width 320–1920 px, A4/A3/A5/Letter/one long page, orientation, margins, block ads, remove pop-ups and cookie banners; local HTML files offline | UI run (local file and example.com) |
| 15 | PDF to JPG | Offline | Page → JPG/PNG/TIFF/BMP or extract embedded images; Normal/High/custom DPI | Unit tests, UI run |
| 16 | PDF to Word | Offline | Built-in layout reconstruction, or Microsoft Word; **OCR for scans** (Premium) included | Unit tests, UI run |
| 17 | PDF to PowerPoint | Offline | Slide per page, picture or editable text; **OCR** included | Unit tests, screen check |
| 18 | PDF to Excel | Offline | Table reconstruction, typed numbers; one sheet or sheet per page; **OCR** included | Unit tests |
| 19 | PDF to PDF/A | Offline | **All 8 conformance levels** (Premium on iLovePDF) | Unit tests |
| 20 | PDF to Markdown | Offline | Headings, lists, tables, links | Unit tests, UI run |
| 21 | Rotate PDF | Offline | Whole files or single pages; all/portrait/landscape filter | Unit tests |
| 22 | Add page numbers | Offline | 9 positions, facing pages, cover page, templates `{n}` `{p}`, font/size/colour | Unit tests, UI run |
| 23 | Add watermark | Offline | Text or image, mosaic, transparency, rotation, layer, page range | Unit tests, UI run |
| 24 | Crop PDF | Offline | Drag a frame on the page, *Fit to content*, all pages or this page | Unit tests, UI run |
| 25 | Edit PDF | Offline | Text, images, rectangles, ellipses, lines, arrows, freehand, white-out, and **editing existing text** (Premium) | Unit tests, UI run |
| 26 | PDF Forms | Offline | Fill fields in place, create text/checkbox/radio/dropdown/list fields, *Detect fields*, flatten | Unit tests, UI run (fill and create) |
| 27 | Unlock PDF | Offline | Removes open passwords and owner restrictions | Unit tests |
| 28 | Protect PDF | Offline | AES-256/AES-128/RC4, every permission flag, password strength meter | Unit tests, UI run (validation) |
| 29 | Sign PDF | Offline (timestamp online) | Draw, type or upload a signature; initials, name, date, text, stamp; **certificate digital signatures** from Windows, a .pfx file or a new self-signed one | Unit tests (signature verifies and is visible), UI run |
| 30 | Redact PDF | Offline | Areas, words, search (match case, whole words, regex) and patterns: email, phone, card numbers with Luhn check, SSN, IBAN, IP, web addresses, dates | Unit tests, UI run |
| 31 | Compare PDF | Offline | Side by side with highlighted changes, change list that jumps to each change, visual overlay; report and highlighted copies | Unit tests, UI run |
| 32 | AI Summarizer | Offline | On-device extractive summary and keywords as Markdown, PDF or text | Unit tests, UI run |
| 33 | Translate PDF | **Online** | MyMemory (free), LibreTranslate or Claude; layout kept | Unit tests (engine); online services not exercised in tests |
| 34 | Workflows | Offline | **Premium-only on iLovePDF**: 3 presets plus your own chains of 14 step types | Unit tests, screen check |

Not included: iLovePDF's *request signatures from other people*. It needs an online service
that sends emails and stores documents, which goes against PageSmith running on your PC.

### 1.2 Things that work across the app

- **Native Windows 11 look.**
  - WPF-UI Fluent controls, Mica backdrop, Fluent System Icons and the Windows accent colour.
  - Light, dark, or follow Windows.
- **Navigation.**
  - Side menu grouped like iLovePDF's categories, with tool search.
  - Home page with category filters and a drop zone that suggests tools for the files you drop.
- **Files.**
  - Drag and drop, file cards with thumbnails, drag to reorder.
  - Page grid with Shift-select, zoom slider and per-page actions.
- **Page editor** (Crop, Edit, Forms, Sign, Redact):
  - zoom (Ctrl + mouse wheel, Ctrl +/−) and a page thumbnail strip;
  - select, move and resize with handles;
  - undo and redo (Ctrl+Z, Ctrl+Y);
  - arrow keys to nudge, Delete to remove, Page Up/Down to change page.
- **Passwords.** An encrypted PDF shows a lock on its card that you click to enter the password;
  editors and tools ask for it when they open or run. Restriction-only PDFs just work.
- **Results.**
  - Open the file, show it in its folder, copy text output.
  - *Continue with…* sends the result straight into another tool.
- **Offline-first.**
  - The title bar shows *Online*/*Offline*, rechecked every 45 s and on network changes.
  - Online features (web pages, translation, signature timestamps) show a *You're offline* bar
    with **Retry** and **Network settings** instead of failing.
- **Settings**, saved in `%APPDATA%\PageSmith\settings.json`:
  - theme;
  - where to save (next to the original, a fixed folder, or ask);
  - Office engine;
  - OCR language;
  - translation service;
  - Claude API key for translation, encrypted with Windows DPAPI.
- **About** shows the iLovePDF Free vs Premium catalog next to what PageSmith includes.
- **Command line.** `PageSmith.exe file.pdf` opens the right tool. Developer options are in §5.6.
- **Errors** are logged to `%APPDATA%\PageSmith\pagesmith.log` and shown in-app, never as crash dialogs.

### 1.3 Fixed since 0.1.0

- **Option panels.** PDF to PowerPoint, PDF to Excel and Add watermark referenced a style that
  didn't exist, so their option panels failed to load.
- **Visible digital signature.** It was drawn outside its own box, so it was invisible in every
  PDF viewer. It now shows and prints, and the signature still verifies.
- **Dropdowns.** A filled dropdown now shows its value in Windows' built-in PDF viewer too.
- **Dark mode.** Titles and file names on tool pages were dark on dark.
- **AI Summarizer** is now offline-only in the app. The Claude engine (`SummaryEngine.Claude`,
  `ClaudeService`) is still in `PageSmith.Core`, untouched, for a future online option.

### 1.4 Known issues

- **Slow first thumbnail.** The first thumbnail after start-up can take a few seconds while the
  Windows PDF renderer warms up. Compare's *Visual overlay* takes a few seconds the first time
  for the same reason.
- **Microsoft Office automation can stall** on some documents. PageSmith then kills only the
  Office processes it started (after 3 minutes) and, in *Automatic* mode, falls back to the
  built-in converter. PDF to Word uses the built-in converter by default for this reason.
- **Editing existing text** covers the old line with the paper colour and writes new text on top.
  It doesn't change the text inside the PDF's content. If the PDF's font isn't installed in
  Windows, a similar standard font is used.
- **HTML to PDF** opens pages in its own private browser profile, so pages that need you to be
  signed in show their signed-out version.
- **Offline summarizer quality.** It sometimes picks table header rows as sentences, and counts
  "com" from e-mail addresses as a keyword.
- **Compiler warnings.** A Release build shows about 8 warnings: nullability in
  `MicrosoftOffice.cs` and `WordDiff.cs`, and obsolete PdfPig/PDFsharp APIs. They are harmless
  but should be cleaned up.
- **No installer or code signing yet.** Windows SmartScreen may warn the first time you run a
  downloaded `PageSmith.exe`.

---

## 2. Next steps for v1.0.0

Work happens on `feature/*` branches cut from `develop` (see §6).

### 2.1 Checks that need real hardware or a real network change

- [ ] **Scanner:** scan through *Scan to PDF → Scanner* with a real scanner (flatbed and feeder).
  Also check the messages when no scanner is connected.
- [ ] **Camera:** take photos in *Scan to PDF → Camera* with a webcam. Also check the message
  when Windows blocks camera access (*Settings → Privacy & security → Camera*).
- [ ] **Offline Retry:** with Wi-Fi off, check that HTML to PDF (web address), Translate and Sign
  (with a timestamp) show the offline bar, and that **Retry** works once back online.
- [ ] **Mouse and pen:** in every page-editor tool, check drawing, moving and resizing with mouse,
  touch and pen.

### 2.2 Quality and release

- [ ] Drive every tool through the UI in light and dark mode. Include password-protected inputs
  and big files (500+ pages).
- [ ] Summarizer polish: skip table-like lines; strip e-mail addresses and URLs before keyword
  scoring.
- [ ] Fix the Release-build warnings.
- [ ] Accessibility pass: keyboard navigation through every screen, automation names for
  icon-only buttons, high-contrast theme check.
- [ ] Add screenshots to `README.md`.
- [ ] Release:
  1. Set `<Version>1.0.0</Version>` in `Directory.Build.props`.
  2. Create `release/1.0.0` from `develop`.
  3. Run `scripts/publish.ps1 -Zip`.
  4. Merge into `main` and tag `v1.0.0`.
  5. Merge back into `develop`.
  6. Attach the zip to a GitHub release.
- [ ] Nice to have:
  - an installer (MSIX or Inno Setup) with *Open with* file associations and Start-menu shortcut;
  - code signing;
  - GitHub Actions CI (`windows-latest`: build, test, publish artifact);
  - an Explorer context menu ("Compress with PageSmith", "Merge with PageSmith").

---

## 3. Free vs Premium: what PageSmith does differently

iLovePDF limits the free plan by:

- files per task (for example 25 for Merge, 2 for Compress);
- file size (15–400 MB per task);
- pages (50 for OCR, 30 for Translate/Markdown);
- Premium-only options: split by size, OCR in PDF→Office, PDF/A, editing existing text, digital
  signatures and Workflows.

PageSmith has **no tiers**: every Free and Premium option is included, with no file, size or page
limits beyond your PC's memory. The full per-tool limit table is in
[docs/FEATURES.md §3](docs/FEATURES.md).

---

## 4. Next steps for v2.0.0: machine-learning features

Each item gets its own `feature/ml-*` branch. Models run on-device. When a model has to be
downloaded, PageSmith asks first, checks the connection, and offers *Retry* if offline.

- [ ] **Auto-orientation and deskew** for scans and OCR, using the Windows OCR text angle plus
  a projection-profile fallback.
- [ ] **Smart scan:** document edge detection and perspective correction for camera photos. This
  builds on `PageSmith.Core/Imaging/DocumentFilters.cs`, which already has the shadow-removing
  black & white filter.
- [ ] **Better summaries:** TextRank over sentence embeddings, using a MiniLM ONNX model through
  ONNX Runtime, downloaded on first use.
- [ ] **PII detection for Redact:** names, addresses and ID numbers beyond the regex presets
  (NER model).
- [ ] **Table detection by clustering** word positions, for more reliable PDF → Excel and Word tables.
- [ ] **SSIM-guided compression:** pick the lowest JPEG quality per image that keeps visual
  similarity above a threshold.
- [ ] **Smart split by topic:** segment long PDFs where the subject changes (TextTiling or embeddings).
- [ ] **Form field detection from page images:** lines, boxes and labels → fillable fields.
- [ ] Release `v2.0.0` the same way as v1.0.0.

---

## 5. How to build PageSmith

### 5.1 Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 version 2004 (build 19041) or later | Windows 11 recommended for Mica and the full Fluent look. x64 is tested; `win-arm64` builds should work but are untested. |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later) | Developed with 10.0.401. Install with `winget install Microsoft.DotNet.SDK.10`. Check with `dotnet --version`. |
| Git | To clone the repository. |
| Internet access for the **first** build only | NuGet downloads the packages once. After that, building and running work offline. |
| An IDE (optional) | Visual Studio 2026 with the *.NET desktop development* workload, JetBrains Rider, or VS Code with C# Dev Kit. The command line alone is enough. |

Optional components the app uses at run time if they are present:

| Component | Used for | If missing |
|---|---|---|
| Microsoft Edge WebView2 Runtime | HTML to PDF | Preinstalled on Windows 11. On Windows 10 install the Evergreen runtime from Microsoft; the app explains this if it's missing. |
| A scanner with a WIA driver | Scan to PDF → Scanner | Most scanners install one. Without a scanner, use the camera or photos. |
| A camera, with *Let desktop apps access your camera* on | Scan to PDF → Camera | The camera window explains how to allow access. |
| Microsoft Office (Word, Excel, PowerPoint) | Best-fidelity Office → PDF, optional Word reflow for PDF → Word | LibreOffice is used if installed; otherwise the built-in converter. |
| LibreOffice | Office → PDF when Office isn't installed | Built-in converter. |
| Windows OCR languages | OCR PDF, OCR in PDF → Office | The English OCR pack ships with English Windows. Add more in **Settings → Time & language → Language & region → Add a language**. Or, in an admin PowerShell: `Get-WindowsCapability -Online \| Where-Object Name -like 'Language.OCR*'`, then `Add-WindowsCapability -Online -Name "Language.OCR~~~fr-FR~0.0.1.0"`. |
| Anthropic API key | Optional Claude translation | Paste it in **Settings → AI services**, or set the `ANTHROPIC_API_KEY` environment variable. Everything else works without it. |

### 5.2 Get the code

```bash
git clone https://github.com/Randula-Gunathilake/PageSmith---pdfEditor-for-windows.git PageSmith
```

```bash
cd PageSmith
```

- `main` holds released versions; the newest is tagged `v0.2.0`.
- `develop` has the latest integrated work.

To build the newest code:

```bash
git checkout develop
```

### 5.3 Build and run (debug)

From the repository root:

```bash
dotnet restore PageSmith.slnx
```

```bash
dotnet build PageSmith.slnx
```

```bash
dotnet run --project src/PageSmith.App
```

- The debug app is at `src/PageSmith.App/bin/Debug/net10.0-windows10.0.19041.0/PageSmith.exe`.
  You can start it directly.
- In Visual Studio or Rider, open `PageSmith.slnx`, set **PageSmith.App** as the startup
  project, and press F5.
- To open files at start-up, put them after `--`.
  - A PDF opens Organize.
  - Several PDFs open Merge.
  - Images open JPG to PDF.

  ```bash
  dotnet run --project src/PageSmith.App -- "C:\Docs\report.pdf"
  ```

### 5.4 Run the tests

```bash
dotnet test tests/PageSmith.Tests
```

- **82 tests, about a minute.** They build real PDFs, render them, and check the output of every
  engine. Some render the output with Windows' PDF engine to check that things are actually
  visible: the signature box, filled dropdowns, black & white scans.
- **Optional parts.** The OCR test runs only if a Windows OCR language is installed. The
  Microsoft Office test runs only if Word is installed. Otherwise they return early and count
  as passed.
- **One test touches the network.** `Connectivity_check_returns_quickly` only checks that the
  connection check answers quickly, so it also passes offline.

To run a single group:

```bash
dotnet test tests/PageSmith.Tests --filter "FullyQualifiedName~OrganizeTests"
```

### 5.5 Build a release (runs on PCs without .NET)

The script runs the tests, then publishes a self-contained build into
`artifacts/publish/<runtime>`:

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

| Option | Effect |
|---|---|
| *(none)* | `win-x64`, one self-contained `PageSmith.exe` (≈ 187 MB) |
| `-Zip` | Also writes `artifacts/PageSmith-<version>-<runtime>.zip` (≈ 71 MB) |
| `-Runtime win-arm64` | Builds for ARM64 PCs (untested) |
| `-Folder` | Regular folder output instead of a single file (faster start-up, many files) |
| `-SkipTests` | Skips the test run |

Equivalent manual command:

```bash
dotnet publish src/PageSmith.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish/win-x64
```

A smaller, framework-dependent build needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on the target PC.
This variant is untested.

```bash
dotnet publish src/PageSmith.App -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64-fdd
```

`artifacts/` is git-ignored. Copy the `.exe` (or the zip) to any Windows 10/11 x64 PC and run
it; nothing needs installing.

### 5.6 Developer notes

- **Project layout:**

  ```text
  PageSmith.slnx
  Directory.Build.props        shared target framework, version, nullable settings
  docs/FEATURES.md             iLovePDF analysis and PageSmith mapping
  scripts/publish.ps1          release build script
  src/PageSmith.Core/          all PDF work, no UI: Tools/, Pdf/, Text/, Imaging/, Ocr/, Office/, Intelligence/, Workflows/, Net/
  src/PageSmith.App/           WPF app:
    Controls/                  FileList, PageGrid, PageEditor (+ ElementVisual), CompareView, ScanWorkspace,
                               HtmlWorkspace, SignaturePad, CameraWindow, ToolShell, ResultView
    Tools/                     one controller per tool; Tools/Editor/ holds the page-editor tools;
                               OptionTemplates.xaml and EditorTemplates.xaml are their option panels
    Infrastructure/            registry, settings, services, WebPageCapture (HTML), ScanSources (WIA)
    Pages/                     Home, Workflows, Settings, About
  tests/PageSmith.Tests/       xUnit tests for the engines
  tools/IconGen/               renders Assets/PageSmith.ico and PNG icons
  ```

- **Adding a tool:**
  1. Write the engine in `PageSmith.Core/Tools/...` with tests.
  2. Add a controller in `PageSmith.App/Tools/`. Derive from `EditorController` if the tool
     places things on pages.
  3. Add a `DataTemplate` for its options in `Tools/OptionTemplates.xaml` (or
     `EditorTemplates.xaml`).
  4. Add a `ToolDescriptor` in `Infrastructure/ToolRegistry.cs`.
  5. Add a page type in `Infrastructure/ToolPages.cs`.
- **Screenshot mode,** for checking screens without clicking:

  ```bash
  PageSmith.exe --page compress --files "a.pdf;b.pdf" --theme dark --width 1360 --height 880 --delay 4000 --snapshot shot.png
  ```

  - `--run` presses the tool's button before the screenshot.
  - `--demo` sets the tool up first: places sample text, shapes, signatures and redaction marks
    through the same code paths as clicks, fills forms, or picks a change in Compare.
  - `--demo-alt` shows the tool's dialog or other view instead (the signature pad, Compare's
    overlay).
  - Snapshot windows don't take keyboard focus, so they can run while you use the PC.
  - `--page` also accepts `home`, `workflows`, `settings` and `about`.
- **"File is locked by PageSmith" build errors** mean the app is still running. Close it, or run
  `Stop-Process -Name PageSmith`.
- **Icons:** WPF-UI's `SymbolIcon` can only draw Fluent icons whose code point is in the Basic
  Multilingual Plane. Some icons (for example `Flow24`, `PenSparkle24`) render as boxes; pick
  another one.
- **Tool pages** opt out of the navigation view's scroll viewer, so they fit the window. They set
  their own theme text colour for the same reason (see `ToolPages.cs`).
- **PDFsharp is pinned to 6.2.4 (MIT).** Check the licence before upgrading, because later
  releases are not MIT-licensed.
- **Text files are UTF-8 without BOM.** When scripting edits in Windows PowerShell 5.1, pass
  `-Encoding UTF8` or use `[IO.File]::ReadAllText(path, UTF8Encoding)`. Otherwise characters
  like "…" and "·" get mangled.
- **App data:**
  - `%APPDATA%\PageSmith\` holds `settings.json`, `workflows.json` and `pagesmith.log`.
  - `%LOCALAPPDATA%\PageSmith\WebView2` is HTML to PDF's browser profile.
  - Scanned pages and photos wait in `%TEMP%\PageSmith\Scans` until they're turned into a PDF.

---

## 6. Branches and commits

| Branch | Purpose |
|---|---|
| `main` | Released versions only; every merge is tagged (`v0.1.0`, `v0.2.0`, `v1.0.0`, …) |
| `develop` | Integration branch; always builds and passes tests |
| `feature/<name>` | One feature each (`feature/ui-tools`, `feature/ml-deskew`, …), merged into `develop` with `--no-ff` |
| `release/<version>` | Final checks and version bump, merged into `main` (tagged) and back into `develop` |

Commit messages follow Conventional Commits: `feat(scope): …`, `fix(scope): …`, `docs: …`,
`build: …`, `test: …`.

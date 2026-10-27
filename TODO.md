# PageSmith — status, roadmap and build guide

PageSmith is a native Windows 11 PDF reader and toolbox. It opens PDFs in tabs like Adobe
Acrobat, and it recreates every tool of the iLovePDF web app, Free and Premium tiers, as an
offline desktop app that works on the open document. This file tracks:

- what works today;
- what is left for v1.0 and the machine-learning v2.0;
- how to build, test and package the app.

For the iLovePDF research behind the feature list (plans, every tool's options, the Free vs
Premium limits), see [docs/FEATURES.md](docs/FEATURES.md).

**Current version:** 0.4.0 (preview), last updated 2026-10-28.

---

## 1. Where things stand

| Area | State |
|---|---|
| iLovePDF analysis | Done: 34 features (33 tools + Workflows), Free vs Premium limits, mapping → [docs/FEATURES.md](docs/FEATURES.md) |
| PDF engine (`PageSmith.Core`) | Done for all 33 tools and the reader; 113 automated tests, all passing |
| Online AI services (new in 0.4.0) | Claude, Gemini or OpenAI, picked from a list, for summaries and translation, with your own API keys (§1.3) |
| Windows app (`PageSmith.App`) | **All 33 tool screens working**, plus Home, Workflows, Settings and About |
| PDF reader (new in 0.3.0) | **Working:** documents in tabs, viewer, find, text selection, comments, print, and every PDF tool on the open document with undo (§1.2) |
| Still to check by hand | A real scanner, a real camera, a real printer, *Retry* with the PC actually offline, and opening PDFs from Explorer (§2.1) |
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
| 32 | AI Summarizer | Offline, or online with an AI service | On-device extractive summary and keywords, or a written summary by Claude, Gemini or OpenAI in the language you pick; Markdown, PDF or text | Unit tests (engine, AI requests and answers), UI run (on-device) |
| 33 | Translate PDF | **Online** | MyMemory (free), LibreTranslate, Claude, Gemini or OpenAI; layout kept | Unit tests (engine, AI requests and answers); online services not exercised in tests |
| 34 | Workflows | Offline | **Premium-only on iLovePDF**: 3 presets plus your own chains of 14 step types | Unit tests, screen check |

Not included: iLovePDF's *request signatures from other people*. It needs an online service
that sends emails and stores documents, which goes against PageSmith running on your PC.

### 1.2 The reader (new in 0.3.0)

PageSmith opens documents the way Adobe Acrobat does: each one in its own tab, ready to read,
and every tool works on the document in front of you.

- **Opening documents.**
  - **Open** button, **Ctrl+O**, the **+** next to the tabs, Home's *Open a file* and *Recent
    documents*, dropping files on the window or the tab strip, or `PageSmith.exe file.pdf`.
  - **One window:** opening a PDF from Explorer while PageSmith runs adds a tab to the open
    window instead of starting a second copy.
  - **Settings → Opening PDFs** adds PageSmith to Windows' *Open with* list for PDFs, then opens
    Windows' *Default apps* page, where you can make it the default PDF app. Windows doesn't let
    apps make themselves the default, so that last click is yours.
  - **Images, Word, Excel, PowerPoint and web pages** open as new PDFs, unsaved until you save
    them. Password-protected PDFs ask for their password.
  - Opening a file that is already open just switches to its tab.
- **Tabs.**
  - Home (every tool, Workflows, Settings) is the first tab, then one tab per document.
  - A dot marks unsaved changes; the window title shows it too.
  - Close with ×, a middle click or **Ctrl+W**. Switch with a click, **Ctrl+Tab** or
    **Ctrl+PgUp/PgDn**.
  - Right-click a tab: close, close others, close all, show in folder, copy the file path.
  - Closing a tab or the window with unsaved changes asks *Save*, *Don't save* or *Cancel*.
- **Viewer.**
  - All pages in one continuous scroll. Only pages in or near the view are rendered, and they are
    re-rendered sharp after zooming, so 300-page documents open and scroll quickly.
  - Zoom: **Ctrl + mouse wheel** (around the pointer), **Ctrl +/−**, the zoom box (type any
    percentage), *Fit width* (**Ctrl+2**), *Fit page* (**Ctrl+0**), actual size (**Ctrl+1**).
  - Page box (**Ctrl+G**), previous/next page, Home/End, Space, Page Up/Down.
  - *Select* tool: drag to select text in reading order (columns first, then lines), across pages
    too. Double-click selects a word. **Ctrl+A** selects all, **Ctrl+C** copies.
  - *Hand* tool (or hold Space, or drag with the middle button) moves the pages around.
  - **Links** work: links inside the document go to their page; web and e-mail links show where
    they go and ask before opening. Other kinds of links are not opened.
- **Side panels.**
  - **Pages:** thumbnails that follow the page you're reading. Right-click for *Rotate*, *Delete*,
    *Extract to a new tab*, *Insert pages from a file* and *Insert a blank page*. Drag thumbnails
    to move pages. Drop PDFs or images on the thumbnails to insert them at that spot.
  - **Bookmarks:** the document's outline; click to go there.
  - **Comments:** every highlight, note, drawing and other comment, with author and date. Click
    one to jump to it, press Delete to remove it.
- **Find (Ctrl+F).** Searches the whole document in the background. Shows *n of N*, highlights
  every match, with Enter/Shift+Enter or F3/Shift+F3 to move between them. *Match case* and
  *Whole words* options. A scan with no text offers *Make it searchable (OCR)*.
- **Comments.**
  - Right-click selected text: **Highlight**, **Underline**, **Strikethrough** or **Redact**.
  - The *Add a note* tool on the toolbar, or right-click → *Add a note here*, places a sticky note.
  - All of them are standard PDF annotations that other PDF readers show too. Hover one to read it.
- **Every tool on the open document.** The **All tools** pane (with search) lists every PDF tool.
  - Tools that change the document (Merge, Remove/Organize pages, Compress, Repair, OCR, PDF/A,
    Rotate, Page numbers, Watermark, Crop, Edit, Forms, Unlock, Protect, Sign, Redact) replace it
    in its tab when they finish.
  - The other tools (Split, Extract pages, conversions, Compare, Summarize, Translate) save new
    files next to the document. A PDF result opens in a new tab.
  - Each change is a new version: **Undo** (**Ctrl+Z**) and **Redo** (**Ctrl+Y**) step through
    them, and the file on disk changes only when you save.
- **Save** (**Ctrl+S**) and **Save as** (**Ctrl+Shift+S**). If the file can't be written (open in
  another program, read-only folder), PageSmith offers *Save as*.
- **Print** (**Ctrl+P**): all pages, the current page or a range, turned to match the paper and
  shrunk to fit when needed.
- **Document properties** (**Ctrl+D**): title, author, dates, producer, PDF version, pages, page
  size, file size, security, forms and bookmarks.
- The reader remembers which side panel and whether the tools pane were open.

### 1.3 Things that work across the app

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
  - Open the file (a PDF opens in a tab), show it in its folder, copy text output.
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
  - **AI services:** an API key and a model for each of Claude (Anthropic), Gemini (Google) and
    OpenAI. Keys are encrypted with Windows DPAPI, or read from `ANTHROPIC_API_KEY`,
    `GEMINI_API_KEY` (or `GOOGLE_API_KEY`) and `OPENAI_API_KEY`. *Load models* lists the models
    your key can use; *Test* sends a one-word request to check the key and model.
- **Online AI services (0.4.0).** Summarize and Translate PDF each have a list to pick the service:
  this PC (summaries only), MyMemory or LibreTranslate (translation only), Claude, Gemini or
  OpenAI. All three AI services get the same instructions, so results read alike.
  - Claude uses Anthropic's official SDK (default model `claude-opus-5-5`), with structured JSON
    output for translations and the server-side fallback when a safety classifier declines.
  - Gemini uses the Gemini API's `generateContent` (default `gemini-3.8-flash`) with a JSON
    response schema.
  - OpenAI uses the Responses API (default `gpt-6-astra`) with a strict JSON schema, and
    `store: false` so OpenAI doesn't keep your documents for later retrieval.
  - Document text leaves the PC only when you pick one of these services, and only to that
    company. They check the connection first and offer *Retry* when offline; a missing or wrong
    key, a model the key can't use, rate limits and refusals each get a clear message.
- **About** shows the iLovePDF Free vs Premium catalog next to what PageSmith includes.
- **Command line.** `PageSmith.exe file.pdf …` opens the files in tabs. Developer options are in §5.6.
- **Errors** are logged to `%APPDATA%\PageSmith\pagesmith.log` and shown in-app, never as crash dialogs.

### 1.4 Fixed since 0.1.0

- **Touchpad and wheel scrolling (0.3.1).**
  - In the reader, a touchpad barely scrolled. A touchpad sends many small scroll steps between two
    screen updates, and each step started from the old position, so only the last one counted. The
    steps now add up, and a touchpad scrolls as far as your fingers move.
  - Home, Settings, Workflows and About didn't scroll with the wheel or a touchpad at all; only the
    scroll bar worked. Their content sits in the navigation pane's scroll area, and an inner scroll
    area with nothing to scroll swallowed every wheel step. Steps now pass on to the area around.
  - Everywhere else, WPF scrolled three lines per touchpad step, however small, so lists raced. Each
    step now scrolls in proportion to the movement; a mouse-wheel notch still scrolls three lines and
    follows Windows' *lines to scroll* setting.
  - In the reader, sideways two-finger scrolling (when zoomed in) and pinch to zoom now work smoothly,
    and pinch zoom in the page editors follows the size of the pinch.

- **Window size (0.2.1).** The window always opened at 1360 × 880, which is taller than the
  usable screen on common laptops (for example 1920 × 1080 at 125% scaling leaves 1536 × 816),
  so its bottom was cut off. It now opens at 85% × 90% of the screen's usable area, centred and
  at most 1440 × 940, or maximized on small screens (under 1280 × 800). After that it reopens
  where you left it, maximized or not. A position that no longer fits, for example after
  unplugging a monitor, is pulled back on screen.
- **Option panels.** PDF to PowerPoint, PDF to Excel and Add watermark referenced a style that
  didn't exist, so their option panels failed to load.
- **Font size boxes (0.3.0).** In Add page numbers and Add watermark the font size box was so
  narrow that its spin buttons hid the number. The number now shows; type it or use the arrow keys.
- **Visible digital signature.** It was drawn outside its own box, so it was invisible in every
  PDF viewer. It now shows and prints, and the signature still verifies.
- **Dropdowns.** A filled dropdown now shows its value in Windows' built-in PDF viewer too.
- **Dark mode.** Titles and file names on tool pages were dark on dark.
- **AI Summarizer.** In 0.2.0 the summarizer was offline-only in the app. Since 0.4.0 it can also
  use Claude, Gemini or OpenAI, picked from a list (§1.3).

### 1.5 Known issues

- **Reader.**
  - Moving, inserting or deleting pages from the thumbnails (like Organize PDF and Remove pages)
    builds a new file from the pages, so bookmarks and links between pages are not kept. Rotating
    pages keeps everything.
  - Windows' PDF engine draws sticky notes with its own orange icon. Other readers show the note's
    own yellow icon.
  - Printing sends each page as a 300 dpi image. It looks right, but large print jobs are big, and
    PageSmith waits while the pages are prepared.
  - Selecting across table columns highlights whole lines, the way most readers do.
  - Form fields are filled with the *PDF Forms* tool, not by typing on the page in the viewer.
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
  touch and pen. In the reader, check selecting text, the hand tool and dragging thumbnails.
- [ ] **AI services with real keys:** in Settings, *Test* and *Load models* for Claude, Gemini and
  OpenAI; then summarize (short and long, another language) and translate a few pages with each.
  The automated tests check requests and answers offline; a fake-key call confirmed each service's
  address and key header, but no real summary has been made yet.
- [ ] **Printer:** print a document (all pages, a range, the current page) on a real printer, in
  portrait and landscape.
- [ ] **Opening from Explorer:** use *Settings → Opening PDFs*, choose PageSmith in Windows'
  *Default apps*, then double-click PDFs in Explorer with PageSmith closed and while it runs (they
  should open as tabs in the same window). Check *Remove* takes PageSmith out of *Open with*.

### 2.2 Quality and release

- [ ] Drive every tool through the UI in light and dark mode. Include password-protected inputs
  and big files (500+ pages).
- [ ] Summarizer polish: skip table-like lines; strip e-mail addresses and URLs before keyword
  scoring.
- [ ] Reader polish:
  - fill form fields by typing on the page;
  - two-page and single-page views, *Rotate view*, full-screen reading mode;
  - reopen last session's tabs and remember the last page of each document;
  - keep bookmarks when pages are moved, inserted or deleted;
  - print text as text (vector) instead of page images.
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

- `main` holds released versions; the newest is tagged `v0.3.1`.
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
- To open files at start-up, put them after `--`. Each opens in a tab; images, Office files and
  web pages open as new PDFs.

  ```bash
  dotnet run --project src/PageSmith.App -- "C:\Docs\report.pdf"
  ```

- **Only one PageSmith runs per user.** Starting it while it is already running hands the files to
  the running window and exits. To try a new build, close the running PageSmith first (snapshot
  runs, §5.6, are not affected).

### 5.4 Run the tests

```bash
dotnet test tests/PageSmith.Tests
```

- **AI service tests** (`AiServiceTests`) check each service's request and how its answers,
  refusals, cut-off answers, model lists and errors are read, against the documented JSON. They
  send nothing, so they need neither the internet nor API keys.
- **113 tests, under a minute.** They build real PDFs, render them, and check the output of every
  engine. Some render the output with Windows' PDF engine to check that things are actually
  visible: the signature box, filled dropdowns, black & white scans, highlights and notes.
- **Reader tests** (`ReaderTests`) cover text search and selection, bookmarks, links, comments,
  document properties, the single-window hand-off (over a private pipe name) and the *Open with*
  registration (under `HKEY_CURRENT_USER\Software\PageSmithTests`, never your real file
  associations).
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
    Controls/                  the reader: DocumentTabs, DocumentView (toolbar, panels, find, tool mode),
                               PdfViewer (continuous page view);
                               tools: FileList, PageGrid, PageEditor (+ ElementVisual), CompareView,
                               ScanWorkspace, HtmlWorkspace, SignaturePad, CameraWindow, ToolShell, ResultView
    Documents/                 DocumentSession (one open document: versions, undo, save), DocumentWorkspace
                               (tabs, open, save, close), PageActions (thumbnail and comment changes)
    Tools/                     one controller per tool; Tools/Editor/ holds the page-editor tools;
                               OptionTemplates.xaml and EditorTemplates.xaml are their option panels
    Infrastructure/            registry, settings, services, PdfPrinter, WebPageCapture (HTML), ScanSources (WIA)
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
  - Snapshot runs never write `settings.json`, so `--theme` and test files don't leak into your
    own settings.
  - `--page` also accepts `home`, `workflows`, `settings` and `about`.
  - **Reader:** `--files` without `--page` opens the files in tabs. Then:
    - `--goto 12` goes to a page, `--select-all` selects all text, and `--drag 200,280,560,370`
      drags the mouse between two points of the page view (same steps as a real drag);
    - `--find "text"` searches, `--highlight "text"` highlights the first match, and
      `--note "text"` adds a note on page 1;
    - `--panel pages|bookmarks|comments|none` picks the side panel, `--properties` opens the
      properties dialog;
    - `--wheel -15,20` sends 20 wheel steps of −15 (a touchpad burst; −120 is a mouse notch) to
      whatever is in the middle of the window, on any page, through the same routed events as real
      ones;
    - `--doc-tool rotate` opens a tool on the document; add `--run` (and `--demo`) to run it,
      which replaces the document like the real button does.

  ```bash
  PageSmith.exe --files "report.pdf" --find "revenue" --panel bookmarks --theme dark --snapshot reader.png
  ```
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
- **Window size and position** come from `Infrastructure/WindowSizing.cs`. The last position is
  saved as `Window` in `settings.json`; delete that entry to get the default size again.
- **App data:**
  - `%APPDATA%\PageSmith\` holds `settings.json`, `workflows.json` and `pagesmith.log`.
  - `%LOCALAPPDATA%\PageSmith\WebView2` is HTML to PDF's browser profile.
  - Scanned pages and photos wait in `%TEMP%\PageSmith\Scans` until they're turned into a PDF.
  - Each open document works on private copies in `%TEMP%\PageSmith\Documents\<id>`: one folder
    per version (for undo) plus tool results. The folder is deleted when its tab closes.
  - *Settings → Opening PDFs* writes only under `HKEY_CURRENT_USER`: the `PageSmith.Document`
    ProgID, `.pdf\OpenWithProgids`, `Applications\PageSmith.exe`, `Software\PageSmith\Capabilities`
    and `RegisteredApplications`. *Remove* deletes them again.
- **Wheel and touchpad scrolling** for every scroll view is handled in
  `Infrastructure/WheelScrolling.cs` (registered at start-up); the reader's page view does its own in
  `PdfViewer.OnWheel`. Scroll views apply a new offset at their next layout pass, so code that scrolls
  several times in a row must continue from the offset it last asked for, not `VerticalOffset`.
- **The reader's rendering** uses `Windows.Data.Pdf` through `PdfRenderer`, which serializes
  renders. Disposing a renderer waits for a render in progress, so a viewer can swap renderers
  when the document changes. Text for find and selection comes from `TextIndex` (PdfPig), read
  page by page as needed.

---

## 6. Branches and commits

| Branch | Purpose |
|---|---|
| `main` | Released versions only; every merge is tagged (`v0.1.0`, `v0.2.0`, `v1.0.0`, …) |
| `develop` | Integration branch; always builds and passes tests |
| `feature/<name>` | One feature each (`feature/ui-tools`, `feature/reader`, `feature/ml-deskew`, …), merged into `develop` with `--no-ff` |
| `release/<version>` | Final checks and version bump, merged into `main` (tagged) and back into `develop` |

Commit messages follow Conventional Commits: `feat(scope): …`, `fix(scope): …`, `docs: …`,
`build: …`, `test: …`.

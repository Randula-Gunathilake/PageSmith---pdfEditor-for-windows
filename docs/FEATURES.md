# iLovePDF feature analysis → PageSmith

This document records what iLovePDF (https://www.ilovepdf.com, inspected 2026-10-24) offers,
how its **Free** and **Premium** tiers differ, and how each feature is implemented in
**PageSmith**, the native Windows 11 desktop application in this repository.

The data below was collected from the public site: the tool grid on the home page, the
pricing page, each tool's option sidebar, and the `ilovepdfConfig.allFileLimits` object that
the site ships to the browser (it holds `registered_limits` for free accounts and
`premium_limits` for paying accounts).

## 1. Plans

| | Basic (Free) | Premium (A$7/mo yearly, A$11 monthly) | Business |
|---|---|---|---|
| Tools | "Essential" tools; some options are Premium-only | All tools | All tools |
| Batch processing | Limited (files per task, see §3) | Unlimited* | Unlimited |
| File size per task | Limited (15–400 MB) | Up to 4 GB | Up to 4 GB |
| Workflows | – | ✔ | ✔ |
| Desktop & mobile apps | – | ✔ (Web, Desktop, Mobile) | ✔ |
| Digital signatures (AES) | – | 5 | Custom |
| Signature requests | Limited | Unlimited | Unlimited |
| Signature management, audit trail, reminders, templates, branding | – | ✔ | ✔ |
| AI credits (Summarize, Translate, Smart split/redact, PDF→Markdown) | Small allowance | 2,000 | Custom |
| Ads | Yes | Ad-free | Ad-free |
| Support | Basic | Priority | Dedicated manager |
| Regional file processing | – | 7 regions | 11 regions |
| Teams, volume discount | – | ✔ | ✔ + SSO, custom contracts |

\*"Unlimited" in marketing; the config still caps batch sizes (e.g. 500 files for merge).

## 2. Tool catalog

Categories follow the site's filter chips: Organize, Optimize, Convert, Edit, Security,
Intelligence, Workflows.

| # | Tool | Category | iLovePDF options (Premium-only options in **bold**) |
|---|---|---|---|
| 1 | Merge PDF | Organize | Drag to reorder, sort A→Z / Z→A, add/remove/rotate files, password prompt |
| 2 | Split PDF | Organize | Range mode (custom ranges, fixed every N pages, merge ranges into one file); Pages mode (extract all / select pages, merge extracted); **Size mode (max size per file, allow compression)**; Smart mode (AI split by document category, preset or custom prompt — AI credits) |
| 3 | Remove pages | Organize | Click thumbnails (Shift for ranges) or type pages |
| 4 | Extract pages | Organize | Same as Split → Pages mode |
| 5 | Organize PDF | Organize | Thumbnail grid: reorder, rotate, delete, add blank page, add more files |
| 6 | Scan to PDF | Organize | Phone camera capture; orientation, page size (Fit/A4/Letter), margin, merge into one |
| 7 | Compress PDF | Optimize | Extreme / Recommended / Less compression |
| 8 | Repair PDF | Optimize | Automatic recovery |
| 9 | OCR PDF | Optimize | 100+ document languages; free accounts: 50 pages/task, **advanced OCR** in Premium |
| 10 | JPG to PDF | Convert | JPG/PNG/TIFF; orientation, page size, margin, merge into one |
| 11 | Word to PDF | Convert | DOC, DOCX, ODT, RTF … |
| 12 | PowerPoint to PDF | Convert | PPT, PPTX, ODP, PPS … |
| 13 | Excel to PDF | Convert | XLS, XLSX, ODS … |
| 14 | HTML to PDF | Convert | URL; screen size (320–1920 px); page size A3/A4/A5/Letter/one long page; orientation; margin; block ads; remove overlay pop-ups |
| 15 | PDF to JPG | Convert | Page to JPG or extract images; quality Normal/High |
| 16 | PDF to Word | Convert | No OCR / **OCR** |
| 17 | PDF to PowerPoint | Convert | No OCR / **OCR** |
| 18 | PDF to Excel | Convert | No OCR / **OCR**; one sheet or one sheet per page |
| 19 | PDF to PDF/A | Convert | **Conformance PDF/A-1b, 1a, 2b, 2u, 2a, 3b, 3u, 3a** (tool flagged Premium) |
| 20 | PDF to Markdown | Convert | Headings, lists, tables, links (AI credits) |
| 21 | Rotate PDF | Edit | Rotate files/pages left/right; filter all/portrait/landscape |
| 22 | Add page numbers | Edit | Single/facing pages, first page cover, 9 positions, margin, first number, page range, text template `{n}`, `{p}`, `{r}`, `{rf}`, font, size, colour |
| 23 | Add watermark | Edit | Text or image; font, size, colour, B/I/U; 9 positions or mosaic; transparency 0/25/50/75 %; rotation 0/45/90/180/270; page range; layer over/below content |
| 24 | Crop PDF | Edit | Drag area to keep; all pages / current page |
| 25 | Edit PDF | Edit | Add text, images, shapes, freehand; font, size, colour; z-order; **edit existing text** (Premium) |
| 26 | PDF Forms | Edit | Detect fields automatically (AI), create text fields, checkboxes, choices, lists; fill forms |
| 27 | Unlock PDF | Security | Remove passwords and restrictions |
| 28 | Protect PDF | Security | Open password; advanced permissions (printing, modification, copy, comments, form filling, accessibility, assembly) |
| 29 | Sign PDF | Security | Simple signature (signature, initials, name, date, text, company stamp); **digital signature (certificate)**; request signatures from others (limited for free) |
| 30 | Redact PDF | Security | Select areas, search text, presets (credit card, phone, email), Smart redact (AI) |
| 31 | Compare PDF | Security | Semantic text comparison with change report; content overlay |
| 32 | AI Summarizer | Intelligence | Summary of a PDF (25,000 words free / 100,000 Premium) |
| 33 | Translate PDF | Intelligence | AI translation keeping layout; 100+ source languages (30 pages free / 500 Premium) |
| 34 | Workflows | Workflows | **Chain tools into reusable workflows** (Premium only) |

Cross-cutting features: drag and drop, Google Drive/Dropbox import, password prompt for
protected inputs, "continue with another tool" on the result page, 2-hour file retention.

## 3. Free vs Premium limits per tool (`allFileLimits`)

| Tool key | Free: max MB / files / pages | Premium: max MB / files / pages |
|---|---|---|
| merge | 100 / 25 | 4000 / 500 |
| split | 100 / 1 | 4000 / 1 |
| compress | 200 / 2 | 4000 / 10 |
| pdfocr | 15 / 1 / 50 | 4000 / 10 / 1000 |
| pdfword | 15 / 1 | 4000 / 10 |
| pdfpowerpoint | 15 / 1 | 4000 / 10 |
| pdfexcel | 15 / 1 / 500 | 4000 / 10 / 1000 |
| wordpdf, officepdf | 15 / 1 | 4000 / 10 |
| imagepdf | 40 / 20 | 4000 / 80 |
| pdfjpg | 25 / 2 | 4000 / 10 |
| pdfa | 100 / 1 | 4000 / 10 |
| rotate | 100 / 20 | 4000 / 80 |
| unlock | 100 / 2 | 4000 / 10 |
| protect | 100 / 2 | 4000 / 80 |
| repair | 100 / 1 | 4000 / 10 |
| organize | 100 / 5 | 4000 / 20 |
| watermark | 100 / 2 | 4000 / 10 |
| pagenumber | 100 / 2 | 4000 / 10 |
| pdfcrop | 100 / 1 | 4000 / 1 |
| editpdf | 100 / 1 | 100 / 1 |
| sign | 50 / 3 | 50 / 5 |
| redact | 400 / 1 | 400 / 1 |
| compare | 400 / 2 | 400 / 2 |
| formfiller | 15 / 1 | 100 / 1 |
| translate | 15 / 1 / 30 | 200 / 1 / 500 |
| summarize | 50 / 1 / 25,000 words, 1 task | 50 / 1 / 100,000 words |
| pdfmarkdown | 15 / 1 / 30 | 200 / 1 / 500 |

## 4. PageSmith mapping

PageSmith has no tiers: every Free and Premium feature is available, with no file, page or
batch limits other than the machine's memory. Everything runs locally unless the row says
**online**; online tools check connectivity first and offer *Retry* or *Network settings*.

| Tool | PageSmith implementation | Network |
|---|---|---|
| Merge, Split (range, fixed, pages, **size**, smart), Remove, Extract, Organize | PDFsharp page import; size split searches for the largest page run that fits; smart split uses bookmarks and blank separator pages (v1), text segmentation (v2) | Offline |
| Scan to PDF | Windows Image Acquisition scanner dialog, camera window with live preview (Windows.Media.Capture), photo import; colour, greyscale or black & white clean-up (adaptive threshold that removes shadows); v2 adds document edge detection, perspective correction and deskew | Offline |
| Compress | Re-encodes and downsamples JPEG and Flate images, recompresses streams, removes unused objects; three levels | Offline |
| Repair | PDFsharp rebuild → PdfPig lenient rebuild → rasterized rebuild | Offline |
| OCR | Windows.Media.Ocr (on-device recognition) with invisible text layer; all installed Windows OCR languages | Offline |
| Images to PDF | JPG, PNG, BMP, GIF, TIFF (multi-page), orientation, size, margin, merge | Offline |
| Word / PowerPoint / Excel to PDF | Microsoft Office (COM) → LibreOffice → built-in OpenXML renderer | Offline |
| HTML to PDF | Hidden Microsoft Edge WebView2: screen width 320–1920 px, A4/A3/A5/Letter/one long page, orientation, margins, ad blocking, pop-up and cookie-banner removal, preview; local HTML files offline, web addresses **online** with *Retry* | Online for URLs |
| PDF to JPG | Windows.Data.Pdf rendering (JPG/PNG/TIFF/BMP), embedded image extraction | Offline |
| PDF to Word | Microsoft Word PDF reflow, or built-in layout reconstruction (OpenXML); optional OCR | Offline |
| PDF to PowerPoint | Slide per page (exact image or editable text boxes) | Offline |
| PDF to Excel | Table reconstruction from word positions; one sheet or sheet per page; optional OCR | Offline |
| PDF to PDF/A | All 8 conformance levels: XMP metadata, sRGB output intent, encryption/JS removal, optional rasterization of pages with non-embedded fonts | Offline |
| PDF to Markdown | Headings by font size, lists, tables, links | Offline |
| Rotate, Page numbers, Watermark, Crop | Full option sets listed above; Crop uses a draggable frame on the page with *Fit to content* | Offline |
| Edit PDF | Page editor: text, images, rectangles, ellipses, lines, arrows, freehand ink, white-out; **edit existing text** covers a line with its paper colour and rewrites it in a matching font | Offline |
| PDF Forms | Fill AcroForm fields in place; create text/checkbox/radio/dropdown/list fields; auto-detect blanks; flatten | Offline |
| Unlock / Protect | AES-256 / AES-128 / RC4 encryption, all permission flags; owner-restriction removal | Offline |
| Sign | Signature pad (draw, type in a handwriting font, upload with background removal); initials, name, date, text, stamp; **digital signature** with a certificate from Windows, a PFX file or a new self-signed one, optional trusted timestamp (online). Requesting signatures from others is not included (needs an online service) | Offline |
| Redact | Areas, clicked words, search (case, whole word, regex) and presets (email, phone, card with Luhn check, SSN, IBAN, URL, IP, dates) with a review list; redacted pages are rasterized so the content is gone | Offline |
| Compare | Side-by-side view with word-level highlights and a change list, visual overlay; writes highlighted copies and a change report | Offline |
| Summarizer | On-device extractive summary and keywords (v1 frequency scoring, v2 TextRank + embeddings). A Claude engine exists in PageSmith.Core but is not offered in the app | Offline |
| Translate | Layout-preserving translation through MyMemory, LibreTranslate or Claude | **Online** |
| Workflows | Reusable chains of tools with saved options, run on many files | Offline |
| Continue with another tool | Every result can be passed into the next tool | – |

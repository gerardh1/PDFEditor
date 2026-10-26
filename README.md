# ArcoCat PDF Editor — Windows PDF Reader & Editor (C# / .NET 8 WPF)

A native Windows desktop PDF Reader & Editor application (**ArcoCat PDF Editor**) featuring a custom transparent **Adobe PDF + White & Orange Cartoon Cat** icon (`ArcoCatIcon.png` / `ArcoCatIcon.ico`), built with **C# (.NET 8 WPF)**, **PDFtoImage / PDFium** (high-DPI page rendering), **UglyToad.PdfPig** (text block coordinate extraction, bookmarks, and full-text search), and **PDFsharp** (PDF modification, annotation/text-edit baking, watermarks, redaction, and page organization).

---

## Included Features

### 1. PDF Reader & Navigation
- **Multi-Tab Document Interface**: Open, close (`Ctrl+W`), and reopen (`Ctrl+Shift+T`) multiple PDF documents simultaneously.
- **Left Navigation Sidebar**:
  - **Pages (Thumbnails)**: Live page thumbnails with page dimensions and single-click navigation.
  - **Outline (Bookmarks)**: Reads native PDF outlines or automatically extracts section headings; add custom bookmarks with `+ Add`.
  - **Search (`Ctrl+F`)**: Full-text search across all pages with snippet previews, `Prev`/`Next` (`F3`/`Shift+F3`) navigation, and visual yellow/orange spotlight boxes around matched text on the page.
- **Zoom & Pan Controls**:
  - Zoom slider (`50%` – `300%`), `Fit Page`, `Fit Width`, `Ctrl + Mouse Wheel` zoom, and `Hand Pan` tool.

### 2. Edit PDF Content (Acrobat "Edit PDF" Mode)
- **Edit Existing PDF Text (`✏ Edit PDF Text`)**:
  - Automatically detects existing text lines on the PDF page (`PdfPig`) and outlines them with dashed blue frames.
  - Click any text block on the page to edit or replace its text in-place, change font family (`Arial`, `Segoe UI`, `Times New Roman`, `Calibri`, `Courier New`), font size, bold/italic, or text color.
- **Add Text Box (`🔤 Add Text Box`)**: Click anywhere on a page to insert new multiline text boxes.
- **Add & Resize Image (`🖼 Add Image...`)**: Insert PNG/JPEG/BMP images onto any page, drag to position, and resize using corner/edge handles or Width/Height controls.
- **Watermarks (`💧 Watermark...`)**: Add diagonal or horizontal watermarks (`CONFIDENTIAL`, `DRAFT`, `APPROVED`, or custom text) with full Undo/Redo (`Ctrl+Z` / `Ctrl+Y`) support.
- **Headers, Footers & Bates Page Numbers (`# Page Numbers`)**: Add running headers and `Page X of Y` footers across all pages.

### 3. Comment, Markup & Annotations
- **Text Markup**: `🖍 Highlight`, `U̲ Underline`, `S̶ Strikethrough`.
- **Sticky Notes (`💬 Sticky Note`)**: Place comment cards with author name and review notes.
- **Freehand Ink & Shapes**: `✒ Draw Ink` (freehand pen), `▭ Box` (rectangle), `◯ Circle` (ellipse), and `↗ Arrow` (line with arrowhead).
- **Rubber Stamps (`🏷 Stamp`)**: Place `APPROVED`, `CONFIDENTIAL`, `DRAFT`, `FINAL`, `FOR REVIEW`, `VOID`, or `URGENT` stamps.
- **Comments & Inspector Panel (Right Sidebar)**: Edit text, dimensions, font, colors, stroke width, and opacity of any selected annotation, or jump directly to any annotation in the document.

### 4. Fill & Sign + Redaction
- **Digital Signature Pad (`✍ Fill & Sign...`)**: Draw your signature with the mouse/stylus or type a styled script signature, choose ink color (`Black`, `Navy Blue`, `Crimson Red`), and place/resize it on any page.
- **Redaction (`⬛ Redact Area`)**: Drag over sensitive text/account numbers to black out with a `REDACTED` overlay, then click **`🔥 Apply & Flatten`** or **`💾 Save`** to bake permanently into the PDF.

### 5. Organize Pages & Convert
- **Page Manipulation**: `↺ Rotate L`, `↻ Rotate R`, `➕ Blank Page`, `📋 Duplicate`, `⬆ Move Up`, `⬇ Move Down`, `🗑 Delete Page`.
- **Merge & Split**: `🔗 Merge PDFs...` (combine multiple PDFs) and `✂ Extract Page...` (export current page to a standalone PDF).
- **Export**: `🖼 Export PNG` (220-DPI page image) and `📝 Export Text` (full text extraction).

---

## How to Install, Uninstall, or Run on Windows

### Option 1: Standalone Windows Setup Installer (`ArcoCatPdfEditor_Setup.exe`)
A self-contained Windows x64 installer with the custom **ArcoCat** file icon (no .NET runtime pre-installation required on the target machine) is built at:
- `d:\PDFEditor\ArcoCatPdfEditor_Setup.exe` (and `d:\PDFEditor\AcrobatPdfEditor_Setup.exe`)
- `d:\PDFEditor\installer\Output\ArcoCatPdfEditor_Setup.exe`

**Installer & Uninstaller features:**
- Interactive Setup Wizard GUI with custom **ArcoCat** icon (customizable installation path, Desktop & Start Menu shortcuts, Windows Explorer `"Edit with ArcoCat PDF Editor"` `.pdf` context menu integration, **Uninstall Existing** button, and `Uninstall.exe` registered in Windows *Installed Apps*).
- Silent install: `ArcoCatPdfEditor_Setup.exe --silent` (or custom path `--dir="C:\Path"`).
- Uninstall options:
  1. From inside the app: **`File -> 🗑 Uninstall Application...`**
  2. Double-click **`uninstall-app.bat`** in `d:\PDFEditor`
  3. Run **`%LOCALAPPDATA%\Programs\ArcoCatPdfEditor\Uninstall.exe`** (or from Start Menu -> *Uninstall ArcoCat PDF Editor*)
  4. Windows **Settings -> Installed Apps -> ArcoCat PDF Editor -> Uninstall**
  5. Run **`ArcoCatPdfEditor_Setup.exe`** and click **Uninstall Existing** (or `ArcoCatPdfEditor_Setup.exe --uninstall`)

### Option 2: Double-click Launcher
Run `run-app.bat` in `d:\PDFEditor`.

### Option 3: .NET CLI
```powershell
cd d:\PDFEditor
dotnet run --project AcrobatPdfEditor/AcrobatPdfEditor.csproj -c Release
```

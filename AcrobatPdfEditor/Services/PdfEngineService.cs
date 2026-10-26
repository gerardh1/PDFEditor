using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcrobatPdfEditor.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace AcrobatPdfEditor.Services
{
    public class PdfEngineService
    {
        public PdfEngineService()
        {
            WindowsFontResolver.EnsureRegistered();
        }

        /// <summary>
        /// Renders a specific 0-based page from PDF bytes to a WPF BitmapSource at the specified DPI.
        /// </summary>
        public BitmapSource RenderPageToBitmapSource(byte[] pdfBytes, int pageIndex, int dpi = 144)
        {
            using var skBitmap = PDFtoImage.Conversion.ToImage(
                pdfBytes,
                page: pageIndex,
                options: new PDFtoImage.RenderOptions(Dpi: dpi));

            return ConvertSkBitmapToBitmapSource(skBitmap);
        }

        private static BitmapSource ConvertSkBitmapToBitmapSource(SKBitmap skBitmap)
        {
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = ms;
            bitmapImage.EndInit();
            bitmapImage.Freeze();
            return bitmapImage;
        }

        /// <summary>
        /// Inspects the PDF document and populates page count, page sizes (in 72-DPI points), metadata, and bookmarks.
        /// </summary>
        public void PopulateDocumentMetadata(PdfDocumentSession session)
        {
            session.PageSizesPt.Clear();
            session.Bookmarks.Clear();

            using var pdfPigDoc = UglyToad.PdfPig.PdfDocument.Open(session.WorkingPdfBytes);
            session.PageCount = pdfPigDoc.NumberOfPages;
            session.PdfVersion = pdfPigDoc.Version.ToString();

            var info = pdfPigDoc.Information;
            session.DocTitle = info?.Title ?? session.FileName;
            session.DocAuthor = info?.Author ?? Environment.UserName;
            session.DocSubject = info?.Subject ?? "PDF Document";

            for (int i = 1; i <= pdfPigDoc.NumberOfPages; i++)
            {
                var page = pdfPigDoc.GetPage(i);
                session.PageSizesPt[i - 1] = new Size(page.Width, page.Height);
            }

            // Try native PDF bookmarks first
            if (pdfPigDoc.TryGetBookmarks(out var bookmarks) && bookmarks.Roots.Any())
            {
                foreach (var node in bookmarks.Roots)
                {
                    AddBookmarkNodesRecursive(node, 0, session.Bookmarks);
                }
            }

            // If no native bookmarks, auto-detect headings from pages
            if (session.Bookmarks.Count == 0)
            {
                for (int i = 0; i < session.PageCount; i++)
                {
                    var blocks = ExtractPageTextBlocks(session.WorkingPdfBytes, i);
                    var topHeadings = blocks
                        .Where(b => b.FontSize >= 13.5 && b.Text.Trim().Length >= 3)
                        .OrderBy(b => b.Y)
                        .Take(3)
                        .ToList();

                    if (topHeadings.Count > 0)
                    {
                        foreach (var h in topHeadings)
                        {
                            session.Bookmarks.Add(new PdfBookmarkItem
                            {
                                Title = h.Text.Length > 55 ? h.Text.Substring(0, 55) + "..." : h.Text,
                                PageNumber = i + 1,
                                Level = h.FontSize >= 17 ? 0 : 1
                            });
                        }
                    }
                    else
                    {
                        session.Bookmarks.Add(new PdfBookmarkItem
                        {
                            Title = $"Page {i + 1}",
                            PageNumber = i + 1,
                            Level = 0
                        });
                    }
                }
            }
        }

        private static void AddBookmarkNodesRecursive(BookmarkNode node, int level, IList<PdfBookmarkItem> target)
        {
            int pageNum = 1;
            if (node is DocumentBookmarkNode docNode && docNode.PageNumber > 0)
            {
                pageNum = docNode.PageNumber;
            }

            target.Add(new PdfBookmarkItem
            {
                Title = string.IsNullOrWhiteSpace(node.Title) ? "Section" : node.Title,
                PageNumber = pageNum,
                Level = level
            });

            foreach (var child in node.Children)
            {
                AddBookmarkNodesRecursive(child, level + 1, target);
            }
        }

        /// <summary>
        /// Extracts text lines/blocks with top-left 72-DPI page coordinates for in-place "Edit PDF Text" mode.
        /// </summary>
        public List<DetectedPdfTextBlock> ExtractPageTextBlocks(byte[] pdfBytes, int pageIndex)
        {
            var result = new List<DetectedPdfTextBlock>();
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages)
                return result;

            var page = doc.GetPage(pageIndex + 1);
            double pageHeight = page.Height;
            var words = page.GetWords().ToList();
            if (words.Count == 0)
                return result;

            // Sort words top-to-bottom (in top-left coordinates), then left-to-right
            var wordItems = words.Select(w =>
            {
                var box = w.BoundingBox;
                double topY = pageHeight - box.Top;
                double height = Math.Max(8, box.Height);
                double fontSize = w.Letters.Count > 0
                    ? Math.Max(8, w.Letters.Average(l => l.PointSize > 0 ? l.PointSize : box.Height))
                    : height;
                return new
                {
                    Word = w,
                    Text = w.Text,
                    X = box.Left,
                    Y = topY,
                    Right = box.Right,
                    Bottom = topY + height,
                    Width = Math.Max(4, box.Width),
                    Height = height,
                    FontSize = fontSize,
                    FontName = w.FontName ?? "Arial"
                };
            })
            .OrderBy(w => Math.Round(w.Y / 4.0) * 4.0)
            .ThenBy(w => w.X)
            .ToList();

            // Group words on the same horizontal line when close together
            var currentLine = new List<dynamic>();

            void FlushCurrentLine()
            {
                if (currentLine.Count == 0)
                    return;

                double minX = currentLine.Min(i => (double)i.X);
                double minY = currentLine.Min(i => (double)i.Y);
                double maxRight = currentLine.Max(i => (double)i.Right);
                double maxBottom = currentLine.Max(i => (double)i.Bottom);
                double avgFont = currentLine.Average(i => (double)i.FontSize);
                string combinedText = string.Join(" ", currentLine.Select(i => (string)i.Text));
                string fontName = (string)currentLine[0].FontName;

                result.Add(new DetectedPdfTextBlock
                {
                    PageIndex = pageIndex,
                    Text = combinedText,
                    X = Math.Max(0, minX - 1),
                    Y = Math.Max(0, minY - 1),
                    Width = Math.Max(16, (maxRight - minX) + 4),
                    Height = Math.Max(12, (maxBottom - minY) + 3),
                    FontSize = Math.Round(avgFont, 1),
                    FontName = NormalizeFontFamilyName(fontName)
                });

                currentLine.Clear();
            }

            foreach (var item in wordItems)
            {
                if (currentLine.Count == 0)
                {
                    currentLine.Add(item);
                    continue;
                }

                var prev = currentLine[^1];
                double yDiff = Math.Abs((double)item.Y - (double)prev.Y);
                double xGap = (double)item.X - (double)prev.Right;
                double lineTolerance = Math.Max(5.0, (double)prev.Height * 0.55);
                double maxWordGap = Math.Max(28.0, (double)prev.FontSize * 2.5);

                if (yDiff <= lineTolerance && xGap >= -4 && xGap <= maxWordGap)
                {
                    currentLine.Add(item);
                }
                else
                {
                    FlushCurrentLine();
                    currentLine.Add(item);
                }
            }

            FlushCurrentLine();
            return result;
        }

        private static string NormalizeFontFamilyName(string rawFontName)
        {
            if (string.IsNullOrWhiteSpace(rawFontName))
                return "Arial";
            string lower = rawFontName.ToLowerInvariant();
            if (lower.Contains("times")) return "Times New Roman";
            if (lower.Contains("courier") || lower.Contains("consolas") || lower.Contains("mono")) return "Courier New";
            if (lower.Contains("segoe")) return "Segoe UI";
            if (lower.Contains("calibri")) return "Calibri";
            return "Arial";
        }

        /// <summary>
        /// Full-text search across all pages (and optional unflattened overlay annotations) with precise bounding boxes and context snippets.
        /// </summary>
        public List<PdfSearchMatch> SearchText(
            byte[] pdfBytes,
            string query,
            IEnumerable<PdfAnnotationItem>? overlayAnnotations = null)
        {
            var matches = new List<PdfSearchMatch>();
            if (string.IsNullOrWhiteSpace(query))
                return matches;

            string trimmedQuery = query.Trim();
            var annotationList = overlayAnnotations?.ToList() ?? new List<PdfAnnotationItem>();

            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);

            for (int pageNum = 1; pageNum <= doc.NumberOfPages; pageNum++)
            {
                int pageIndex = pageNum - 1;
                var blocks = ExtractPageTextBlocks(pdfBytes, pageIndex);

                foreach (var block in blocks)
                {
                    if (string.IsNullOrEmpty(block.Text))
                        continue;

                    // Skip blocks that have been covered/replaced by an EditText annotation on the same page
                    bool replacedByEdit = annotationList.Any(a =>
                        a.PageIndex == pageIndex &&
                        a.ToolType == EditorTool.EditText &&
                        Math.Abs(a.X - block.X) < 8 &&
                        Math.Abs(a.Y - block.Y) < 8);

                    if (replacedByEdit)
                        continue;

                    int searchStart = 0;
                    while (searchStart < block.Text.Length)
                    {
                        int idx = block.Text.IndexOf(trimmedQuery, searchStart, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            break;

                        double charRatioStart = (double)idx / Math.Max(1, block.Text.Length);
                        double charRatioLen = (double)trimmedQuery.Length / Math.Max(1, block.Text.Length);
                        double subX = block.X + (block.Width * charRatioStart);
                        double subW = Math.Max(18, block.Width * charRatioLen);

                        matches.Add(new PdfSearchMatch
                        {
                            PageNumber = pageNum,
                            MatchText = block.Text.Substring(idx, trimmedQuery.Length),
                            Snippet = block.Text,
                            X = subX,
                            Y = block.Y,
                            Width = subW,
                            Height = block.Height
                        });

                        searchStart = idx + Math.Max(1, trimmedQuery.Length);
                    }
                }

                // Also search any unflattened text annotations on this page
                var pageTextAnnotations = annotationList.Where(a =>
                    a.PageIndex == pageIndex &&
                    !string.IsNullOrWhiteSpace(a.Text) &&
                    (a.ToolType == EditorTool.EditText ||
                     a.ToolType == EditorTool.AddText ||
                     a.ToolType == EditorTool.StickyNote ||
                     a.ToolType == EditorTool.Stamp));

                foreach (var ann in pageTextAnnotations)
                {
                    int idx = ann.Text.IndexOf(trimmedQuery, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        matches.Add(new PdfSearchMatch
                        {
                            PageNumber = pageNum,
                            MatchText = ann.Text.Substring(idx, trimmedQuery.Length),
                            Snippet = $"[{ann.ToolDisplayName}] {ann.Text}",
                            X = ann.X,
                            Y = ann.Y,
                            Width = Math.Max(36, ann.Width),
                            Height = Math.Max(16, ann.Height)
                        });
                    }
                }
            }

            return matches;
        }

        /// <summary>
        /// Bakes all annotations, text edits, stamps, signatures, images, and redactions into a new PDF byte array.
        /// </summary>
        public byte[] BakeAnnotationsIntoPdf(byte[] sourcePdfBytes, IEnumerable<PdfAnnotationItem> annotations)
        {
            var annotationList = annotations?.ToList() ?? new List<PdfAnnotationItem>();
            if (annotationList.Count == 0)
                return sourcePdfBytes;

            using var inputStream = new MemoryStream(sourcePdfBytes);
            using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

            var groupedByPage = annotationList.GroupBy(a => a.PageIndex);
            foreach (var group in groupedByPage)
            {
                if (group.Key < 0 || group.Key >= document.PageCount)
                    continue;

                var page = document.Pages[group.Key];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var item in group)
                {
                    DrawAnnotationOnPdfGraphics(gfx, item);
                }
            }

            using var outStream = new MemoryStream();
            document.Save(outStream, false);
            return outStream.ToArray();
        }

        private static void DrawAnnotationOnPdfGraphics(XGraphics gfx, PdfAnnotationItem item)
        {
            var strokeColor = ParseXColor(item.StrokeColorHex, item.Opacity);
            var fillColor = ParseXColor(item.FillColorHex, item.Opacity);
            var fontStyle = (item.IsBold, item.IsItalic) switch
            {
                (true, true) => XFontStyleEx.BoldItalic,
                (true, false) => XFontStyleEx.Bold,
                (false, true) => XFontStyleEx.Italic,
                _ => XFontStyleEx.Regular
            };
            var font = new XFont(string.IsNullOrWhiteSpace(item.FontFamily) ? "Arial" : item.FontFamily, Math.Max(6, item.FontSize), fontStyle);

            switch (item.ToolType)
            {
                case EditorTool.EditText:
                {
                    var coverColor = ParseXColor(item.CoverBackgroundHex, 1.0);
                    gfx.DrawRectangle(new XSolidBrush(coverColor), item.X - 1, item.Y - 1, Math.Max(8, item.Width + 2), Math.Max(10, item.Height + 2));

                    if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        var textBrush = new XSolidBrush(ParseXColor(item.StrokeColorHex, 1.0));
                        DrawMultilineText(gfx, item.Text, font, textBrush, item.X, item.Y, Math.Max(40, item.Width + 40));
                    }
                    break;
                }

                case EditorTool.AddText:
                {
                    if (!string.IsNullOrWhiteSpace(item.FillColorHex) && !item.FillColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                    {
                        var bgColor = ParseXColor(item.FillColorHex, Math.Min(0.85, item.Opacity));
                        gfx.DrawRectangle(new XSolidBrush(bgColor), item.X, item.Y, Math.Max(20, item.Width), Math.Max(18, item.Height));
                    }

                    if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        var textBrush = new XSolidBrush(strokeColor);
                        DrawMultilineText(gfx, item.Text, font, textBrush, item.X + 3, item.Y + 2, Math.Max(40, item.Width));
                    }
                    break;
                }

                case EditorTool.Highlight:
                {
                    var hiColor = ParseXColor(item.FillColorHex, 0.38);
                    gfx.DrawRectangle(new XSolidBrush(hiColor), item.X, item.Y, Math.Max(4, item.Width), Math.Max(6, item.Height));
                    break;
                }

                case EditorTool.Underline:
                {
                    var pen = new XPen(strokeColor, Math.Max(1.5, item.StrokeThickness));
                    double yLine = item.Y + item.Height - 1;
                    gfx.DrawLine(pen, item.X, yLine, item.X + item.Width, yLine);
                    break;
                }

                case EditorTool.Strikethrough:
                {
                    var pen = new XPen(strokeColor, Math.Max(1.5, item.StrokeThickness));
                    double yMid = item.Y + (item.Height / 2.0);
                    gfx.DrawLine(pen, item.X, yMid, item.X + item.Width, yMid);
                    break;
                }

                case EditorTool.StickyNote:
                {
                    var badgeBrush = new XSolidBrush(ParseXColor(item.FillColorHex, 0.95));
                    var borderPen = new XPen(ParseXColor("#B28900", 1.0), 1.2);
                    gfx.DrawRoundedRectangle(borderPen, badgeBrush, item.X, item.Y, Math.Max(120, item.Width), Math.Max(44, item.Height), 6, 6);

                    var headerFont = new XFont("Arial", 8.5, XFontStyleEx.Bold);
                    var bodyFont = new XFont("Arial", 9.5, XFontStyleEx.Regular);
                    gfx.DrawString($"Note ({item.Author}):", headerFont, XBrushes.Black, new XPoint(item.X + 6, item.Y + 12));
                    if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        DrawMultilineText(gfx, item.Text, bodyFont, XBrushes.Black, item.X + 6, item.Y + 16, Math.Max(110, item.Width - 12));
                    }
                    break;
                }

                case EditorTool.Freehand:
                {
                    if (item.Points.Count >= 2)
                    {
                        var pen = new XPen(strokeColor, Math.Max(1.0, item.StrokeThickness))
                        {
                            LineCap = XLineCap.Round,
                            LineJoin = XLineJoin.Round
                        };
                        var xPoints = item.Points.Select(p => new XPoint(p.X, p.Y)).ToArray();
                        gfx.DrawLines(pen, xPoints);
                    }
                    break;
                }

                case EditorTool.Rectangle:
                {
                    var pen = new XPen(strokeColor, Math.Max(1.0, item.StrokeThickness));
                    gfx.DrawRectangle(pen, item.X, item.Y, Math.Max(2, item.Width), Math.Max(2, item.Height));
                    break;
                }

                case EditorTool.Ellipse:
                {
                    var pen = new XPen(strokeColor, Math.Max(1.0, item.StrokeThickness));
                    gfx.DrawEllipse(pen, item.X, item.Y, Math.Max(2, item.Width), Math.Max(2, item.Height));
                    break;
                }

                case EditorTool.Line:
                {
                    var pen = new XPen(strokeColor, Math.Max(1.5, item.StrokeThickness));
                    double x1 = item.X;
                    double y1 = item.Y;
                    double x2 = item.X + item.Width;
                    double y2 = item.Y + item.Height;
                    if (item.Points.Count >= 2)
                    {
                        x1 = item.Points[0].X;
                        y1 = item.Points[0].Y;
                        x2 = item.Points[^1].X;
                        y2 = item.Points[^1].Y;
                    }
                    gfx.DrawLine(pen, x1, y1, x2, y2);

                    double angle = Math.Atan2(y2 - y1, x2 - x1);
                    double arrowLen = 9 + item.StrokeThickness;
                    var p1 = new XPoint(x2 - arrowLen * Math.Cos(angle - Math.PI / 6), y2 - arrowLen * Math.Sin(angle - Math.PI / 6));
                    var p2 = new XPoint(x2 - arrowLen * Math.Cos(angle + Math.PI / 6), y2 - arrowLen * Math.Sin(angle + Math.PI / 6));
                    gfx.DrawLine(pen, x2, y2, p1.X, p1.Y);
                    gfx.DrawLine(pen, x2, y2, p2.X, p2.Y);
                    break;
                }

                case EditorTool.Stamp:
                {
                    var stampColor = ParseXColor(item.StrokeColorHex, 0.88);
                    var bgColor = ParseXColor(item.StrokeColorHex, 0.12);
                    var pen = new XPen(stampColor, 2.5);
                    double w = Math.Max(90, item.Width);
                    double h = Math.Max(32, item.Height);
                    gfx.DrawRoundedRectangle(pen, new XSolidBrush(bgColor), item.X, item.Y, w, h, 6, 6);

                    string label = string.IsNullOrWhiteSpace(item.StampLabel) ? item.Text : item.StampLabel;
                    var stampFont = new XFont("Arial", Math.Max(11, h * 0.42), XFontStyleEx.Bold);
                    gfx.DrawString(label, stampFont, new XSolidBrush(stampColor), new XRect(item.X, item.Y, w, h), XStringFormats.Center);
                    break;
                }

                case EditorTool.Signature:
                case EditorTool.InsertImage:
                {
                    if (item.ImageBytes != null && item.ImageBytes.Length > 0)
                    {
                        using var xImg = LoadXImageSafe(item.ImageBytes);
                        if (xImg != null)
                        {
                            gfx.DrawImage(xImg, item.X, item.Y, Math.Max(16, item.Width), Math.Max(16, item.Height));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        var sigFont = new XFont("Times New Roman", Math.Max(16, item.FontSize), XFontStyleEx.BoldItalic);
                        gfx.DrawString(item.Text, sigFont, new XSolidBrush(strokeColor), new XPoint(item.X + 4, item.Y + item.Height * 0.72));
                    }
                    break;
                }

                case EditorTool.Redact:
                {
                    gfx.DrawRectangle(XBrushes.Black, item.X, item.Y, Math.Max(6, item.Width), Math.Max(6, item.Height));
                    if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        var redactFont = new XFont("Arial", 8, XFontStyleEx.Bold);
                        gfx.DrawString(item.Text, redactFont, XBrushes.White, new XRect(item.X, item.Y, item.Width, item.Height), XStringFormats.Center);
                    }
                    break;
                }
            }
        }

        private static XImage? LoadXImageSafe(byte[] imageBytes)
        {
            try
            {
                var imgStream = new MemoryStream(imageBytes, 0, imageBytes.Length, writable: false, publiclyVisible: true);
                return XImage.FromStream(imgStream);
            }
            catch
            {
                try
                {
                    using var wpfInStream = new MemoryStream(imageBytes);
                    var decoder = BitmapDecoder.Create(wpfInStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapSource frame = decoder.Frames[0];
                    if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32 && frame.Format != PixelFormats.Bgr24)
                    {
                        frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    }

                    var pngEncoder = new PngBitmapEncoder();
                    pngEncoder.Frames.Add(BitmapFrame.Create(frame));
                    using var wpfOutStream = new MemoryStream();
                    pngEncoder.Save(wpfOutStream);
                    byte[] convertedBytes = wpfOutStream.ToArray();
                    var convertedMs = new MemoryStream(convertedBytes, 0, convertedBytes.Length, writable: false, publiclyVisible: true);
                    return XImage.FromStream(convertedMs);
                }
                catch
                {
                    try
                    {
                        using var skBitmap = SKBitmap.Decode(imageBytes);
                        if (skBitmap != null)
                        {
                            using var skImage = SKImage.FromBitmap(skBitmap);
                            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
                            byte[] pngBytes = data.ToArray();
                            var fallbackMs = new MemoryStream(pngBytes, 0, pngBytes.Length, writable: false, publiclyVisible: true);
                            return XImage.FromStream(fallbackMs);
                        }
                    }
                    catch
                    {
                        // Ignore invalid image data
                    }
                }

                return null;
            }
        }

        private static void DrawMultilineText(XGraphics gfx, string text, XFont font, XBrush brush, double x, double y, double maxWidth)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            double lineHeight = font.Size * 1.22;
            double currentBaselineY = y + font.Size;

            foreach (string rawLine in lines)
            {
                gfx.DrawString(rawLine, font, brush, new XPoint(x, currentBaselineY));
                currentBaselineY += lineHeight;
            }
        }

        private static XColor ParseXColor(string hex, double opacity)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex))
                    return XColor.FromArgb((int)(opacity * 255), 0, 0, 0);

                var mediaColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                int alpha = (int)Math.Clamp(opacity * 255, 0, 255);
                return XColor.FromArgb(alpha, mediaColor.R, mediaColor.G, mediaColor.B);
            }
            catch
            {
                return XColor.FromArgb((int)Math.Clamp(opacity * 255, 0, 255), 229, 57, 53);
            }
        }

        #region Organize Pages & Document Transformations

        public byte[] RotatePage(byte[] pdfBytes, int pageIndex, int deltaDegrees, bool applyToAllPages = false)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);

            for (int i = 0; i < doc.PageCount; i++)
            {
                if (applyToAllPages || i == pageIndex)
                {
                    var page = doc.Pages[i];
                    int newRot = ((page.Rotate + deltaDegrees) % 360 + 360) % 360;
                    page.Rotate = newRot;
                }
            }

            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] DeletePage(byte[] pdfBytes, int pageIndex)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            if (doc.PageCount <= 1 || pageIndex < 0 || pageIndex >= doc.PageCount)
                return pdfBytes;

            doc.Pages.RemoveAt(pageIndex);

            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] DuplicatePage(byte[] pdfBytes, int pageIndex)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var importDoc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            using var outDoc = new PdfDocument();

            for (int i = 0; i < importDoc.PageCount; i++)
            {
                outDoc.AddPage(importDoc.Pages[i]);
                if (i == pageIndex)
                {
                    outDoc.AddPage(importDoc.Pages[i]);
                }
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] MovePage(byte[] pdfBytes, int fromIndex, int toIndex)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var importDoc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            if (fromIndex < 0 || fromIndex >= importDoc.PageCount || toIndex < 0 || toIndex >= importDoc.PageCount || fromIndex == toIndex)
                return pdfBytes;

            var order = Enumerable.Range(0, importDoc.PageCount).ToList();
            int item = order[fromIndex];
            order.RemoveAt(fromIndex);
            order.Insert(toIndex, item);

            using var outDoc = new PdfDocument();
            foreach (int idx in order)
            {
                outDoc.AddPage(importDoc.Pages[idx]);
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] InsertBlankPage(byte[] pdfBytes, int afterPageIndex, double widthPt = 612, double heightPt = 792)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var importDoc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            using var outDoc = new PdfDocument();

            for (int i = 0; i < importDoc.PageCount; i++)
            {
                outDoc.AddPage(importDoc.Pages[i]);
                if (i == afterPageIndex)
                {
                    var blank = outDoc.AddPage();
                    blank.Width = XUnit.FromPoint(widthPt);
                    blank.Height = XUnit.FromPoint(heightPt);
                }
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] MergePdfDocuments(IEnumerable<byte[]> pdfByteList)
        {
            using var outDoc = new PdfDocument();

            foreach (var bytes in pdfByteList)
            {
                using var ms = new MemoryStream(bytes);
                using var importDoc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
                for (int i = 0; i < importDoc.PageCount; i++)
                {
                    outDoc.AddPage(importDoc.Pages[i]);
                }
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] ExtractPageRange(byte[] pdfBytes, int startPage1Based, int endPage1Based)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var importDoc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            using var outDoc = new PdfDocument();

            int start = Math.Clamp(startPage1Based - 1, 0, importDoc.PageCount - 1);
            int end = Math.Clamp(endPage1Based - 1, start, importDoc.PageCount - 1);

            for (int i = start; i <= end; i++)
            {
                outDoc.AddPage(importDoc.Pages[i]);
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] ApplyWatermark(
            byte[] pdfBytes,
            string watermarkText,
            double fontSize = 54,
            string colorHex = "#D32F2F",
            double opacity = 0.22,
            double angleDegrees = -35,
            bool allPages = true,
            int currentPageIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(watermarkText))
                return pdfBytes;

            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            var font = new XFont("Arial", Math.Max(14, fontSize), XFontStyleEx.Bold);
            var brush = new XSolidBrush(ParseXColor(colorHex, opacity));

            for (int i = 0; i < doc.PageCount; i++)
            {
                if (!allPages && i != currentPageIndex)
                    continue;

                var page = doc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var state = gfx.Save();
                gfx.TranslateTransform(page.Width.Point / 2.0, page.Height.Point / 2.0);
                gfx.RotateTransform(angleDegrees);
                var size = gfx.MeasureString(watermarkText, font);
                gfx.DrawString(watermarkText, font, brush, new XPoint(-size.Width / 2.0, size.Height / 4.0));
                gfx.Restore(state);
            }

            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            return outMs.ToArray();
        }

        public byte[] ApplyHeaderFooterPageNumbers(byte[] pdfBytes, string headerLeft, string headerRight, bool includePageNumbers = true)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            var font = new XFont("Arial", 9.5, XFontStyleEx.Regular);
            var brush = new XSolidBrush(XColor.FromArgb(210, 70, 70, 70));

            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                if (!string.IsNullOrWhiteSpace(headerLeft))
                {
                    gfx.DrawString(headerLeft, font, brush, new XPoint(42, 26));
                }

                if (!string.IsNullOrWhiteSpace(headerRight))
                {
                    var rightSize = gfx.MeasureString(headerRight, font);
                    gfx.DrawString(headerRight, font, brush, new XPoint(page.Width.Point - 42 - rightSize.Width, 26));
                }

                if (includePageNumbers)
                {
                    string pageLabel = $"Page {i + 1} of {doc.PageCount}";
                    var labelSize = gfx.MeasureString(pageLabel, font);
                    gfx.DrawString(pageLabel, font, brush, new XPoint((page.Width.Point - labelSize.Width) / 2.0, page.Height.Point - 22));
                }
            }

            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            return outMs.ToArray();
        }

        public string ExtractFullDocumentText(byte[] pdfBytes)
        {
            var sb = new StringBuilder();
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            for (int i = 1; i <= doc.NumberOfPages; i++)
            {
                sb.AppendLine($"==================== PAGE {i} OF {doc.NumberOfPages} ====================");
                var blocks = ExtractPageTextBlocks(pdfBytes, i - 1);
                foreach (var block in blocks)
                {
                    sb.AppendLine(block.Text);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        #endregion

        #region Sample Showcase PDF Generator

        public byte[] CreateSampleShowcasePdfBytes()
        {
            using var doc = new PdfDocument();
            doc.Info.Title = "Enterprise Software & Services Agreement - ArcoCat PDF Editor Showcase";
            doc.Info.Author = "ArcoCat PDF Editor";
            doc.Info.Subject = "Interactive PDF Reader & Editor Feature Guide";

            var titleFont = new XFont("Arial", 20, XFontStyleEx.Bold);
            var subtitleFont = new XFont("Arial", 11, XFontStyleEx.Italic);
            var h1Font = new XFont("Arial", 14, XFontStyleEx.Bold);
            var h2Font = new XFont("Arial", 11.5, XFontStyleEx.Bold);
            var bodyFont = new XFont("Arial", 10.5, XFontStyleEx.Regular);
            var monoFont = new XFont("Courier New", 9.5, XFontStyleEx.Regular);

            var crimsonBrush = new XSolidBrush(XColor.FromArgb(255, 198, 40, 40));
            var darkNavyBrush = new XSolidBrush(XColor.FromArgb(255, 24, 42, 74));
            var bodyBrush = new XSolidBrush(XColor.FromArgb(255, 38, 43, 51));
            var mutedBrush = new XSolidBrush(XColor.FromArgb(255, 105, 115, 130));
            var cardFill = new XSolidBrush(XColor.FromArgb(255, 246, 248, 252));
            var borderPen = new XPen(XColor.FromArgb(255, 210, 218, 230), 1.0);

            // ---------------- PAGE 1: Executive Overview & Editable Content ----------------
            var page1 = doc.AddPage();
            page1.Width = XUnit.FromPoint(612);
            page1.Height = XUnit.FromPoint(792);
            using (var gfx = XGraphics.FromPdfPage(page1))
            {
                gfx.DrawRectangle(crimsonBrush, 0, 0, 612, 10);
                gfx.DrawString("ARCOCAT PDF EDITOR - WINDOWS PDF READER & EDITOR", subtitleFont, crimsonBrush, new XPoint(48, 42));
                gfx.DrawString("Enterprise Master Services Agreement", titleFont, darkNavyBrush, new XPoint(48, 68));
                gfx.DrawString("Document ID: MSA-2026-9841   |   Effective Date: October 26, 2026   |   Status: FOR REVIEW", bodyFont, mutedBrush, new XPoint(48, 90));
                gfx.DrawLine(borderPen, 48, 104, 564, 104);

                gfx.DrawString("1. Executive Summary & In-Place Text Editing", h1Font, darkNavyBrush, new XPoint(48, 132));
                gfx.DrawString("Welcome to ArcoCat PDF Editor for Windows. Every line of text in this PDF is", bodyFont, bodyBrush, new XPoint(48, 154));
                gfx.DrawString("selectable, searchable, and directly editable in-place using the 'Edit PDF Text' tool.", bodyFont, bodyBrush, new XPoint(48, 170));
                gfx.DrawString("Click 'Edit Text' in the top toolbar to see dashed blue bounding boxes around existing", bodyFont, bodyBrush, new XPoint(48, 186));
                gfx.DrawString("PDF text blocks, then click any line to modify its wording, font size, or color.", bodyFont, bodyBrush, new XPoint(48, 202));

                gfx.DrawRoundedRectangle(borderPen, cardFill, 48, 224, 516, 118, 6, 6);
                gfx.DrawString("2. Key Agreement Parties & Commercial Terms", h2Font, darkNavyBrush, new XPoint(64, 246));
                gfx.DrawString("Client Organization: Contoso Global Enterprises Ltd.", bodyFont, bodyBrush, new XPoint(64, 268));
                gfx.DrawString("Solution Provider: Northwind Cloud Systems Inc.", bodyFont, bodyBrush, new XPoint(64, 286));
                gfx.DrawString("Annual Contract Value: $148,500.00 USD (Net 30 Billing Cycle)", bodyFont, bodyBrush, new XPoint(64, 304));
                gfx.DrawString("Primary Contact: alex.morgan@contoso-enterprise.com", bodyFont, bodyBrush, new XPoint(64, 322));

                gfx.DrawString("3. Scope of Deliverables & Milestones", h1Font, darkNavyBrush, new XPoint(48, 372));
                gfx.DrawString("Phase 1: Enterprise Cloud Infrastructure Deployment and Security Audit.", bodyFont, bodyBrush, new XPoint(48, 396));
                gfx.DrawString("Phase 2: Automated Document Workflow & Digital Signature Integration.", bodyFont, bodyBrush, new XPoint(48, 414));
                gfx.DrawString("Phase 3: Production Rollout, SLA Monitoring (99.95% Uptime), and Training.", bodyFont, bodyBrush, new XPoint(48, 432));

                gfx.DrawString("4. Milestone Payment Schedule", h1Font, darkNavyBrush, new XPoint(48, 474));
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 232, 237, 245)), 48, 488, 516, 24);
                gfx.DrawString("Milestone Code", h2Font, darkNavyBrush, new XPoint(58, 504));
                gfx.DrawString("Deliverable Description", h2Font, darkNavyBrush, new XPoint(180, 504));
                gfx.DrawString("Amount (USD)", h2Font, darkNavyBrush, new XPoint(455, 504));

                string[][] rows =
                {
                    new[] { "MS-101", "Platform Architecture & Discovery Sign-off", "$38,500.00" },
                    new[] { "MS-102", "Core Security & Compliance Implementation", "$62,000.00" },
                    new[] { "MS-103", "Final User Acceptance Testing & Go-Live", "$48,000.00" }
                };

                double rowY = 512;
                foreach (var r in rows)
                {
                    gfx.DrawRectangle(borderPen, 48, rowY, 516, 26);
                    gfx.DrawString(r[0], monoFont, bodyBrush, new XPoint(58, rowY + 17));
                    gfx.DrawString(r[1], bodyFont, bodyBrush, new XPoint(180, rowY + 17));
                    gfx.DrawString(r[2], bodyFont, bodyBrush, new XPoint(455, rowY + 17));
                    rowY += 26;
                }

                gfx.DrawString("5. Quick Feature Checklist to Try Right Now", h1Font, darkNavyBrush, new XPoint(48, 622));
                gfx.DrawString("- Use 'Highlight', 'Underline', or 'Sticky Note' to markup any clause above.", bodyFont, bodyBrush, new XPoint(48, 646));
                gfx.DrawString("- Use 'Edit PDF Text' to change '$148,500.00 USD' to a new contract value.", bodyFont, bodyBrush, new XPoint(48, 664));
                gfx.DrawString("- Use 'Stamp' or 'Fill & Sign' to place an APPROVED stamp or drawn signature.", bodyFont, bodyBrush, new XPoint(48, 682));
                gfx.DrawString("- Use 'Redact' to permanently black out sensitive account numbers on Page 2.", bodyFont, bodyBrush, new XPoint(48, 700));

                gfx.DrawString("Page 1 of 3", bodyFont, mutedBrush, new XPoint(280, 756));
            }

            // ---------------- PAGE 2: Confidentiality, Security & Redaction Testbed ----------------
            var page2 = doc.AddPage();
            page2.Width = XUnit.FromPoint(612);
            page2.Height = XUnit.FromPoint(792);
            using (var gfx = XGraphics.FromPdfPage(page2))
            {
                gfx.DrawRectangle(crimsonBrush, 0, 0, 612, 10);
                gfx.DrawString("6. Data Privacy, Security & Confidentiality Terms", h1Font, darkNavyBrush, new XPoint(48, 54));
                gfx.DrawString("Both parties agree to maintain strict confidentiality regarding proprietary technical,", bodyFont, bodyBrush, new XPoint(48, 80));
                gfx.DrawString("financial, and customer records disclosed during the performance of this Agreement.", bodyFont, bodyBrush, new XPoint(48, 98));
                gfx.DrawString("All confidential documents must be encrypted at rest using AES-256 standards.", bodyFont, bodyBrush, new XPoint(48, 116));

                gfx.DrawRoundedRectangle(borderPen, cardFill, 48, 144, 516, 130, 6, 6);
                gfx.DrawString("7. Sensitive Banking & Wire Transfer Details (Redaction Practice Area)", h2Font, crimsonBrush, new XPoint(64, 168));
                gfx.DrawString("Beneficiary Bank: First Federal International Treasury Bank", bodyFont, bodyBrush, new XPoint(64, 192));
                gfx.DrawString("ABA Routing Number: 021000021-9948", monoFont, bodyBrush, new XPoint(64, 212));
                gfx.DrawString("Beneficiary Account Number: ACCT-8849-2041-9930", monoFont, bodyBrush, new XPoint(64, 230));
                gfx.DrawString("SWIFT / BIC Code: FFITUS33XXX   |   Tax ID: 94-3829104", monoFont, bodyBrush, new XPoint(64, 248));

                gfx.DrawString("8. Service Level Agreement (SLA) & Support Response", h1Font, darkNavyBrush, new XPoint(48, 310));
                gfx.DrawString("Severity 1 (Critical Production Outage): Response within 15 minutes (24x7x365).", bodyFont, bodyBrush, new XPoint(48, 336));
                gfx.DrawString("Severity 2 (Major Feature Degradation): Response within 2 business hours.", bodyFont, bodyBrush, new XPoint(48, 354));
                gfx.DrawString("Severity 3 (Standard Inquiry / Minor Defect): Response within 1 business day.", bodyFont, bodyBrush, new XPoint(48, 372));

                gfx.DrawString("9. Intellectual Property & Licensing", h1Font, darkNavyBrush, new XPoint(48, 416));
                gfx.DrawString("Customer retains all right, title, and interest in and to Customer Data. Provider", bodyFont, bodyBrush, new XPoint(48, 442));
                gfx.DrawString("grants Customer a perpetual, worldwide, non-exclusive license to use deliverables.", bodyFont, bodyBrush, new XPoint(48, 460));

                gfx.DrawString("Page 2 of 3", bodyFont, mutedBrush, new XPoint(280, 756));
            }

            // ---------------- PAGE 3: Execution & Digital Signatures ----------------
            var page3 = doc.AddPage();
            page3.Width = XUnit.FromPoint(612);
            page3.Height = XUnit.FromPoint(792);
            using (var gfx = XGraphics.FromPdfPage(page3))
            {
                gfx.DrawRectangle(crimsonBrush, 0, 0, 612, 10);
                gfx.DrawString("10. Governing Law & Dispute Resolution", h1Font, darkNavyBrush, new XPoint(48, 54));
                gfx.DrawString("This Agreement shall be governed by and construed in accordance with the laws of", bodyFont, bodyBrush, new XPoint(48, 80));
                gfx.DrawString("the State of Washington, without regard to its conflict of law principles.", bodyFont, bodyBrush, new XPoint(48, 98));

                gfx.DrawString("11. Authorized Signatures & Execution Block", h1Font, darkNavyBrush, new XPoint(48, 150));
                gfx.DrawString("IN WITNESS WHEREOF, the parties have caused this Enterprise Agreement to be", bodyFont, bodyBrush, new XPoint(48, 176));
                gfx.DrawString("executed by their duly authorized representatives as of the Effective Date.", bodyFont, bodyBrush, new XPoint(48, 194));

                gfx.DrawRoundedRectangle(borderPen, cardFill, 48, 230, 245, 160, 6, 6);
                gfx.DrawString("FOR CLIENT ORGANIZATION:", h2Font, darkNavyBrush, new XPoint(64, 254));
                gfx.DrawString("Contoso Global Enterprises Ltd.", bodyFont, bodyBrush, new XPoint(64, 274));
                gfx.DrawLine(borderPen, 64, 336, 275, 336);
                gfx.DrawString("Authorized Signature (Use 'Fill & Sign')", subtitleFont, mutedBrush, new XPoint(64, 352));
                gfx.DrawString("Name: Alex Morgan, VP of Operations", bodyFont, bodyBrush, new XPoint(64, 372));

                gfx.DrawRoundedRectangle(borderPen, cardFill, 318, 230, 245, 160, 6, 6);
                gfx.DrawString("FOR SOLUTION PROVIDER:", h2Font, darkNavyBrush, new XPoint(334, 254));
                gfx.DrawString("Northwind Cloud Systems Inc.", bodyFont, bodyBrush, new XPoint(334, 274));
                gfx.DrawLine(borderPen, 334, 336, 545, 336);
                gfx.DrawString("Authorized Signature / Official Stamp", subtitleFont, mutedBrush, new XPoint(334, 352));
                gfx.DrawString("Name: Jordan Taylor, General Counsel", bodyFont, bodyBrush, new XPoint(334, 372));

                gfx.DrawString("Page 3 of 3", bodyFont, mutedBrush, new XPoint(280, 756));
            }

            using var ms = new MemoryStream();
            doc.Save(ms, false);
            return ms.ToArray();
        }

        #endregion
    }
}

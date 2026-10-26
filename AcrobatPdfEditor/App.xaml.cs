using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using AcrobatPdfEditor.Models;
using AcrobatPdfEditor.Services;

namespace AcrobatPdfEditor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var svc = new PdfEngineService();
                byte[] pdf = svc.CreateSampleShowcasePdfBytes();
                var session = new PdfDocumentSession { WorkingPdfBytes = pdf, FileName = "SelfTest.pdf" };
                svc.PopulateDocumentMetadata(session);

                var bmp = svc.RenderPageToBitmapSource(pdf, 0, 96);
                var blocks = svc.ExtractPageTextBlocks(pdf, 0);
                var matches = svc.SearchText(pdf, "Contoso");

                var annotations = new List<PdfAnnotationItem>
                {
                    new() { PageIndex = 0, ToolType = EditorTool.EditText, X = 64, Y = 304, Width = 280, Height = 16, Text = "Annual Contract Value: $195,000.00 USD (Updated)", StrokeColorHex = "#1E293B" },
                    new() { PageIndex = 0, ToolType = EditorTool.Highlight, X = 48, Y = 150, Width = 320, Height = 18, FillColorHex = "#FFEB3B" },
                    new() { PageIndex = 0, ToolType = EditorTool.Stamp, X = 400, Y = 50, Width = 140, Height = 40, StampLabel = "APPROVED", StrokeColorHex = "#15803D" },
                    new() { PageIndex = 1, ToolType = EditorTool.Redact, X = 64, Y = 200, Width = 260, Height = 20, Text = "REDACTED" }
                };

                using (var skBmp = new SkiaSharp.SKBitmap(200, 60))
                {
                    using var canvas = new SkiaSharp.SKCanvas(skBmp);
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    using var skImg = SkiaSharp.SKImage.FromBitmap(skBmp);
                    byte[] pngBytes = skImg.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
                    byte[] jpgBytes = skImg.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray();
                    annotations.Add(new PdfAnnotationItem
                    {
                        PageIndex = 0,
                        ToolType = EditorTool.Signature,
                        X = 110,
                        Y = 290,
                        Width = 175,
                        Height = 56,
                        ImageBytes = pngBytes,
                        Text = "Test Signature"
                    });
                    annotations.Add(new PdfAnnotationItem
                    {
                        PageIndex = 0,
                        ToolType = EditorTool.InsertImage,
                        X = 150,
                        Y = 350,
                        Width = 120,
                        Height = 80,
                        ImageBytes = jpgBytes,
                        Text = "Test Jpeg"
                    });
                }

                var sigBorder = new System.Windows.Controls.Border
                {
                    Width = 340,
                    Height = 90,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "Alex Morgan",
                        FontFamily = new System.Windows.Media.FontFamily("Times New Roman"),
                        FontSize = 36,
                        FontStyle = FontStyles.Italic,
                        FontWeight = FontWeights.Bold,
                        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 71, 161)),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                sigBorder.Measure(new Size(340, 90));
                sigBorder.Arrange(new Rect(0, 0, 340, 90));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(340, 90, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(sigBorder);
                var pngEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                pngEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var msPng = new MemoryStream())
                {
                    pngEnc.Save(msPng);
                    annotations.Add(new PdfAnnotationItem
                    {
                        PageIndex = 0,
                        ToolType = EditorTool.Signature,
                        X = 200,
                        Y = 400,
                        Width = 175,
                        Height = 56,
                        ImageBytes = msPng.ToArray(),
                        Text = "WPF PNG Signature"
                    });
                }

                byte[] baked = svc.BakeAnnotationsIntoPdf(pdf, annotations);
                var bakedBmp = svc.RenderPageToBitmapSource(baked, 0, 96);
                var bakedSession = new PdfDocumentSession { WorkingPdfBytes = baked, FileName = "BakedTest.pdf" };
                svc.PopulateDocumentMetadata(bakedSession);
                bakedSession.PushUndoSnapshot();
                byte[] watermarked = svc.ApplyWatermark(baked, "CONFIDENTIAL", 54, "#D32F2F", 0.22, -35, true);
                bakedSession.WorkingPdfBytes = watermarked;
                bool undone = bakedSession.Undo(out bool pdfReverted);
                if (!undone || !pdfReverted || !bakedSession.WorkingPdfBytes.AsSpan().SequenceEqual(baked))
                    throw new InvalidOperationException("Watermark undo failed to restore original PDF bytes.");
                bakedSession.Redo(out _);
                byte[] numbered = svc.ApplyHeaderFooterPageNumbers(watermarked, "Enterprise Agreement", "2026-10-26", true);

                string sampleOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Sample_Enterprise_Agreement.pdf");
                try
                {
                    File.WriteAllBytes(Path.GetFullPath(sampleOut), pdf);
                }
                catch
                {
                    // Ignore path write errors in restricted folders
                }

                Console.WriteLine($"SELF-TEST PASSED: Pages={session.PageCount}, Rendered={bmp.PixelWidth}x{bmp.PixelHeight}, TextBlocksPage1={blocks.Count}, SearchMatches={matches.Count}, FinalPdfBytes={numbered.Length}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELF-TEST FAILED: " + ex.ToString());
                Shutdown(1);
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using PdfSharp.Fonts;

namespace AcrobatPdfEditor.Services
{
    public class WindowsFontResolver : IFontResolver
    {
        private static bool _initialized;
        private static readonly ConcurrentDictionary<string, byte[]> FontDataCache = new(StringComparer.OrdinalIgnoreCase);

        public static void EnsureRegistered()
        {
            if (_initialized)
                return;

            try
            {
                GlobalFontSettings.FontResolver = new WindowsFontResolver();
            }
            catch
            {
                // Ignore if already set
            }
            _initialized = true;
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string normalized = (familyName ?? "Arial").Trim().ToLowerInvariant();
            string fileName;

            if (normalized.Contains("times") || normalized.Contains("serif"))
            {
                fileName = (isBold, isItalic) switch
                {
                    (true, true) => "timesbi.ttf",
                    (true, false) => "timesbd.ttf",
                    (false, true) => "timesi.ttf",
                    _ => "times.ttf"
                };
            }
            else if (normalized.Contains("courier") || normalized.Contains("consolas") || normalized.Contains("mono"))
            {
                fileName = (isBold, isItalic) switch
                {
                    (true, true) => "courbi.ttf",
                    (true, false) => "courbd.ttf",
                    (false, true) => "couri.ttf",
                    _ => "cour.ttf"
                };
            }
            else if (normalized.Contains("segoe"))
            {
                fileName = (isBold, isItalic) switch
                {
                    (true, true) => "segoeuiz.ttf",
                    (true, false) => "segoeuib.ttf",
                    (false, true) => "segoeuii.ttf",
                    _ => "segoeui.ttf"
                };
            }
            else if (normalized.Contains("calibri"))
            {
                fileName = (isBold, isItalic) switch
                {
                    (true, true) => "calibriz.ttf",
                    (true, false) => "calibrib.ttf",
                    (false, true) => "calibrii.ttf",
                    _ => "calibri.ttf"
                };
            }
            else
            {
                fileName = (isBold, isItalic) switch
                {
                    (true, true) => "arialbi.ttf",
                    (true, false) => "arialbd.ttf",
                    (false, true) => "ariali.ttf",
                    _ => "arial.ttf"
                };
            }

            return new FontResolverInfo(fileName);
        }

        public byte[]? GetFont(string faceName)
        {
            return FontDataCache.GetOrAdd(faceName, name =>
            {
                string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                string candidate = Path.Combine(fontsDir, name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllBytes(candidate);
                }

                string fallbackArial = Path.Combine(fontsDir, "arial.ttf");
                if (File.Exists(fallbackArial))
                {
                    return File.ReadAllBytes(fallbackArial);
                }

                string fallbackSegoe = Path.Combine(fontsDir, "segoeui.ttf");
                if (File.Exists(fallbackSegoe))
                {
                    return File.ReadAllBytes(fallbackSegoe);
                }

                throw new FileNotFoundException($"Could not locate Windows font file: {name}");
            });
        }
    }
}

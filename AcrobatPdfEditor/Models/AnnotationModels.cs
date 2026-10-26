using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace AcrobatPdfEditor.Models
{
    public enum EditorTool
    {
        Select,
        Pan,
        EditText,
        AddText,
        Highlight,
        Underline,
        Strikethrough,
        StickyNote,
        Freehand,
        Rectangle,
        Ellipse,
        Line,
        Stamp,
        Signature,
        InsertImage,
        Redact
    }

    public enum AcrobatRibbonTab
    {
        Home,
        EditPdf,
        CommentMarkup,
        FillAndSign,
        OrganizePages,
        ProtectRedact
    }

    public class PdfAnnotationItem : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private double _x;
        private double _y;
        private double _width;
        private double _height;
        private double _fontSize = 12;
        private string _fontFamily = "Arial";
        private string _strokeColorHex = "#E53935";
        private string _fillColorHex = "#FFEB3B";
        private double _opacity = 1.0;
        private double _strokeThickness = 2.0;
        private bool _isBold;
        private bool _isItalic;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int PageIndex { get; set; }
        public EditorTool ToolType { get; set; }

        /// <summary>
        /// Coordinates in standard 72-DPI PDF page points (origin: top-left of page).
        /// </summary>
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        public double Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(); }
        }

        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); OnPropertyChanged(nameof(SummaryText)); }
        }

        public string OriginalText { get; set; } = string.Empty;
        public string CoverBackgroundHex { get; set; } = "#FFFFFF";

        public double FontSize
        {
            get => _fontSize;
            set { _fontSize = Math.Clamp(value, 6, 120); OnPropertyChanged(); }
        }

        public string FontFamily
        {
            get => _fontFamily;
            set { _fontFamily = value; OnPropertyChanged(); }
        }

        public bool IsBold
        {
            get => _isBold;
            set { _isBold = value; OnPropertyChanged(); }
        }

        public bool IsItalic
        {
            get => _isItalic;
            set { _isItalic = value; OnPropertyChanged(); }
        }

        public string StrokeColorHex
        {
            get => _strokeColorHex;
            set { _strokeColorHex = value; OnPropertyChanged(); }
        }

        public string FillColorHex
        {
            get => _fillColorHex;
            set { _fillColorHex = value; OnPropertyChanged(); }
        }

        public double Opacity
        {
            get => _opacity;
            set { _opacity = Math.Clamp(value, 0.1, 1.0); OnPropertyChanged(); }
        }

        public double StrokeThickness
        {
            get => _strokeThickness;
            set { _strokeThickness = Math.Clamp(value, 0.5, 24.0); OnPropertyChanged(); }
        }

        public List<Point> Points { get; set; } = new();
        public byte[]? ImageBytes { get; set; }
        public string Author { get; set; } = Environment.UserName;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string StampLabel { get; set; } = "APPROVED";

        public string ToolDisplayName => ToolType switch
        {
            EditorTool.EditText => "Text Edit",
            EditorTool.AddText => "Text Box",
            EditorTool.Highlight => "Highlight",
            EditorTool.Underline => "Underline",
            EditorTool.Strikethrough => "Strikethrough",
            EditorTool.StickyNote => "Sticky Note",
            EditorTool.Freehand => "Freehand Ink",
            EditorTool.Rectangle => "Rectangle",
            EditorTool.Ellipse => "Ellipse",
            EditorTool.Line => "Arrow / Line",
            EditorTool.Stamp => $"Stamp ({StampLabel})",
            EditorTool.Signature => "Signature",
            EditorTool.InsertImage => "Image",
            EditorTool.Redact => "Redaction",
            _ => ToolType.ToString()
        };

        public string PageDisplay => $"Page {PageIndex + 1}";

        public string SummaryText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Text))
                    return Text.Length > 45 ? Text.Substring(0, 45) + "..." : Text;
                if (ToolType == EditorTool.Stamp)
                    return StampLabel;
                return $"{ToolDisplayName} at ({X:F0}, {Y:F0})";
            }
        }

        public PdfAnnotationItem Clone()
        {
            return new PdfAnnotationItem
            {
                Id = Id,
                PageIndex = PageIndex,
                ToolType = ToolType,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                Text = Text,
                OriginalText = OriginalText,
                CoverBackgroundHex = CoverBackgroundHex,
                FontSize = FontSize,
                FontFamily = FontFamily,
                IsBold = IsBold,
                IsItalic = IsItalic,
                StrokeColorHex = StrokeColorHex,
                FillColorHex = FillColorHex,
                Opacity = Opacity,
                StrokeThickness = StrokeThickness,
                Points = Points.Select(p => new Point(p.X, p.Y)).ToList(),
                ImageBytes = ImageBytes != null ? (byte[])ImageBytes.Clone() : null,
                Author = Author,
                CreatedAt = CreatedAt,
                StampLabel = StampLabel
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class DetectedPdfTextBlock
    {
        public int PageIndex { get; set; }
        public string Text { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double FontSize { get; set; } = 11.0;
        public string FontName { get; set; } = "Arial";
    }

    public class PdfBookmarkItem
    {
        public string Title { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int Level { get; set; }
        public string PageLabel => $"p. {PageNumber}";
    }

    public class PdfSearchMatch
    {
        public int PageNumber { get; set; }
        public string MatchText { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string PageLabel => $"Page {PageNumber}";
    }

    public class PdfThumbnailItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private ImageSource? _thumbnailImage;

        public int PageIndex { get; set; }
        public int PageNumber => PageIndex + 1;
        public string Label => $"Page {PageNumber}";
        public string DimensionsLabel { get; set; } = string.Empty;

        public ImageSource? ThumbnailImage
        {
            get => _thumbnailImage;
            set { _thumbnailImage = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

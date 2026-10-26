using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AcrobatPdfEditor.Models
{
    public class PdfDocumentSession : INotifyPropertyChanged
    {
        private string _fileName = "Untitled.pdf";
        private string? _filePath;
        private bool _isModified;
        private int _currentPageIndex;
        private int _pageCount = 1;
        private double _zoomLevel = 1.0;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");

        public string? FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    FileName = Path.GetFileName(value);
                }
                OnPropertyChanged();
            }
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                _fileName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TabHeader));
            }
        }

        public bool IsModified
        {
            get => _isModified;
            set
            {
                _isModified = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TabHeader));
            }
        }

        public string TabHeader => IsModified ? $"{FileName} *" : FileName;

        public byte[] WorkingPdfBytes { get; set; } = Array.Empty<byte>();

        public int CurrentPageIndex
        {
            get => _currentPageIndex;
            set
            {
                _currentPageIndex = Math.Clamp(value, 0, Math.Max(0, PageCount - 1));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentPageNumber));
            }
        }

        public int CurrentPageNumber => CurrentPageIndex + 1;

        public int PageCount
        {
            get => _pageCount;
            set
            {
                _pageCount = Math.Max(1, value);
                OnPropertyChanged();
            }
        }

        public double ZoomLevel
        {
            get => _zoomLevel;
            set
            {
                _zoomLevel = Math.Clamp(value, 0.25, 4.0);
                OnPropertyChanged();
            }
        }

        public ObservableCollection<PdfAnnotationItem> Annotations { get; } = new();
        public ObservableCollection<PdfThumbnailItem> Thumbnails { get; } = new();
        public ObservableCollection<PdfBookmarkItem> Bookmarks { get; } = new();
        public ObservableCollection<PdfSearchMatch> SearchMatches { get; } = new();

        public Dictionary<int, List<DetectedPdfTextBlock>> PageTextBlocksCache { get; } = new();
        public Dictionary<int, Size> PageSizesPt { get; } = new();

        public string DocTitle { get; set; } = string.Empty;
        public string DocAuthor { get; set; } = string.Empty;
        public string DocSubject { get; set; } = string.Empty;
        public string PdfVersion { get; set; } = "1.7";

        private readonly record struct UndoSnapshot(
            byte[] PdfBytes,
            List<PdfAnnotationItem> Annotations,
            int CurrentPageIndex);

        private readonly Stack<UndoSnapshot> _undoStack = new();
        private readonly Stack<UndoSnapshot> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        private UndoSnapshot CreateCurrentSnapshot()
        {
            return new UndoSnapshot(
                WorkingPdfBytes,
                Annotations.Select(a => a.Clone()).ToList(),
                CurrentPageIndex);
        }

        private bool RestoreSnapshot(UndoSnapshot snapshot)
        {
            bool pdfBytesChanged = !ReferenceEquals(WorkingPdfBytes, snapshot.PdfBytes) &&
                                   !WorkingPdfBytes.AsSpan().SequenceEqual(snapshot.PdfBytes);

            WorkingPdfBytes = snapshot.PdfBytes;
            Annotations.Clear();
            foreach (var item in snapshot.Annotations)
            {
                Annotations.Add(item);
            }

            if (pdfBytesChanged)
            {
                PageTextBlocksCache.Clear();
            }

            CurrentPageIndex = snapshot.CurrentPageIndex;
            return pdfBytesChanged;
        }

        public void PushUndoSnapshot()
        {
            _undoStack.Push(CreateCurrentSnapshot());
            _redoStack.Clear();
            IsModified = true;
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        public bool Undo(out bool pdfBytesChanged)
        {
            pdfBytesChanged = false;
            if (_undoStack.Count == 0)
                return false;

            _redoStack.Push(CreateCurrentSnapshot());
            var previous = _undoStack.Pop();
            pdfBytesChanged = RestoreSnapshot(previous);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return true;
        }

        public bool Undo() => Undo(out _);

        public bool Redo(out bool pdfBytesChanged)
        {
            pdfBytesChanged = false;
            if (_redoStack.Count == 0)
                return false;

            _undoStack.Push(CreateCurrentSnapshot());
            var next = _redoStack.Pop();
            pdfBytesChanged = RestoreSnapshot(next);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            return true;
        }

        public bool Redo() => Redo(out _);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

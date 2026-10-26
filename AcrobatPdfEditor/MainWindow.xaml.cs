using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AcrobatPdfEditor.Models;
using AcrobatPdfEditor.Services;
using AcrobatPdfEditor.Views;
using Microsoft.Win32;

namespace AcrobatPdfEditor
{
    public partial class MainWindow : Window
    {
        private readonly PdfEngineService _pdfService = new();
        private readonly ObservableCollection<PdfDocumentSession> _sessions = new();
        private readonly Stack<PdfDocumentSession> _closedSessions = new();

        private PdfDocumentSession? _activeSession;
        private EditorTool _currentTool = EditorTool.Select;
        private PdfAnnotationItem? _selectedAnnotation;
        private PdfSearchMatch? _highlightedSearchMatch;
        private int _currentSearchMatchIndex = -1;
        private string _lastExecutedSearchQuery = string.Empty;
        private bool _suppressSearchSync;

        // Mouse interaction state on OverlayCanvas
        private bool _isDraggingNewShape;
        private bool _isDraggingExistingItem;
        private bool _isResizingExistingItem;
        private string _activeResizeHandle = "SE";
        private Rect _resizeOriginalBounds;
        private bool _isPanning;
        private Point _mouseDownCanvasPt;
        private Point _panStartMouseWindowPt;
        private Point _panStartScrollOffset;
        private Point _dragItemOriginalPos;
        private PdfAnnotationItem? _activeDrawingAnnotation;

        private bool _suppressInspectorEvents;

        public MainWindow()
        {
            InitializeComponent();
            DocumentTabsList.ItemsSource = _sessions;

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FitWindowToWorkArea(1280, 800);

            var cmdPdfFiles = Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(arg => !arg.StartsWith("--") && File.Exists(arg) && arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (cmdPdfFiles.Count > 0)
            {
                foreach (string file in cmdPdfFiles)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        var session = new PdfDocumentSession
                        {
                            FilePath = file,
                            FileName = System.IO.Path.GetFileName(file),
                            WorkingPdfBytes = bytes,
                            ZoomLevel = 1.25
                        };
                        InitializeAndActivateSession(session);
                    }
                    catch
                    {
                        // Ignore invalid startup file and fall back if none opened
                    }
                }
            }

            if (_sessions.Count == 0)
            {
                CreateAndLoadSampleDocument();
            }

            UpdateToolButtonsVisualState();
            UpdateWorkspacePanelToggleButtons();
        }

        private void FitWindowToWorkArea(double targetWidth, double targetHeight)
        {
            WindowState = WindowState.Normal;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(targetWidth, workArea.Width * 0.94));
            Height = Math.Max(MinHeight, Math.Min(targetHeight, workArea.Height * 0.92));
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2.0);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2.0);
        }

        #region Document & Tab Management

        private void CreateAndLoadSampleDocument()
        {
            byte[] sampleBytes = _pdfService.CreateSampleShowcasePdfBytes();
            var session = new PdfDocumentSession
            {
                FileName = "Enterprise_Agreement_Showcase.pdf",
                WorkingPdfBytes = sampleBytes,
                ZoomLevel = 1.25
            };

            InitializeAndActivateSession(session);
            SetStatus("Loaded 3-page Sample PDF in ArcoCat PDF Editor. Try 'Edit PDF Text', 'Find', 'Fill & Sign', or 'Add Image'!");
        }

        private void InitializeAndActivateSession(PdfDocumentSession session)
        {
            _pdfService.PopulateDocumentMetadata(session);
            RebuildSessionThumbnails(session);

            _sessions.Add(session);
            DocumentTabsList.SelectedItem = session;
            ActivateSession(session);
        }

        private void ActivateSession(PdfDocumentSession? session)
        {
            _activeSession = session;
            _selectedAnnotation = null;
            _highlightedSearchMatch = null;
            _currentSearchMatchIndex = -1;

            if (_activeSession == null)
            {
                PdfPageImage.Source = null;
                OverlayCanvas.Children.Clear();
                ThumbnailsListBox.ItemsSource = null;
                BookmarksListBox.ItemsSource = null;
                SearchResultsListBox.ItemsSource = null;
                AnnotationsListBox.ItemsSource = null;
                if (PagePaperBorder != null) PagePaperBorder.Visibility = Visibility.Collapsed;
                if (EmptyWorkspaceOverlay != null) EmptyWorkspaceOverlay.Visibility = Visibility.Visible;
                PageIndicatorText.Text = "No Document";
                UpdateClosedTabButtonsState();
                return;
            }

            if (PagePaperBorder != null) PagePaperBorder.Visibility = Visibility.Visible;
            if (EmptyWorkspaceOverlay != null) EmptyWorkspaceOverlay.Visibility = Visibility.Collapsed;

            ThumbnailsListBox.ItemsSource = _activeSession.Thumbnails;
            BookmarksListBox.ItemsSource = _activeSession.Bookmarks;
            SearchResultsListBox.ItemsSource = _activeSession.SearchMatches;
            AnnotationsListBox.ItemsSource = _activeSession.Annotations;

            ZoomSlider.Value = _activeSession.ZoomLevel;
            RenderCurrentPageAndOverlay();
            UpdateDocumentInfoPanel();
            SyncInspectorFromSelection();
            UpdateClosedTabButtonsState();
        }

        private void RebuildSessionThumbnails(PdfDocumentSession session)
        {
            session.Thumbnails.Clear();
            session.PageTextBlocksCache.Clear();

            for (int i = 0; i < session.PageCount; i++)
            {
                var thumbBmp = _pdfService.RenderPageToBitmapSource(session.WorkingPdfBytes, i, dpi: 48);
                Size sz = session.PageSizesPt.TryGetValue(i, out var s) ? s : new Size(612, 792);
                session.Thumbnails.Add(new PdfThumbnailItem
                {
                    PageIndex = i,
                    ThumbnailImage = thumbBmp,
                    DimensionsLabel = $"{sz.Width / 72.0:F1}\" × {sz.Height / 72.0:F1}\" ({sz.Width:F0}×{sz.Height:F0} pt)",
                    IsSelected = (i == session.CurrentPageIndex)
                });
            }
        }

        private void RefreshSingleThumbnail(PdfDocumentSession session, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= session.Thumbnails.Count)
                return;

            try
            {
                var previewBytes = _pdfService.BakeAnnotationsIntoPdf(
                    session.WorkingPdfBytes,
                    session.Annotations.Where(a => a.PageIndex == pageIndex));

                session.Thumbnails[pageIndex].ThumbnailImage = _pdfService.RenderPageToBitmapSource(previewBytes, pageIndex, dpi: 48);
            }
            catch
            {
                // Keep existing thumbnail if preview bake fails
            }
        }

        private void DocumentTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DocumentTabsList.SelectedItem is PdfDocumentSession session && session != _activeSession)
            {
                ActivateSession(session);
            }
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PdfDocumentSession session)
            {
                CloseDocumentSession(session);
            }
        }

        private void CloseActiveTab_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession != null)
            {
                CloseDocumentSession(_activeSession);
            }
        }

        private void CloseDocumentSession(PdfDocumentSession session)
        {
            if (session.IsModified)
            {
                var res = MessageBox.Show(
                    $"Save changes to '{session.FileName}' before closing tab?\n(Note: Even without saving to disk, you can reopen this tab in this session via '↺ Reopen Tab'.)",
                    "Close Document Tab",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Cancel)
                    return;
                if (res == MessageBoxResult.Yes)
                {
                    SaveSessionToFile(session, forceDialog: false);
                }
            }

            int closedIndex = _sessions.IndexOf(session);
            _sessions.Remove(session);
            _closedSessions.Push(session);
            UpdateClosedTabButtonsState();

            if (_sessions.Count == 0)
            {
                ActivateSession(null);
                SetStatus($"Closed tab '{session.FileName}'. Click '↺ Reopen Tab' (Ctrl+Shift+T) or '📂 Open' to reopen.");
            }
            else
            {
                int nextIdx = Math.Clamp(closedIndex, 0, _sessions.Count - 1);
                DocumentTabsList.SelectedIndex = nextIdx;
                ActivateSession(_sessions[nextIdx]);
                SetStatus($"Closed tab '{session.FileName}'. Click '↺ Reopen Tab' (Ctrl+Shift+T) to restore it.");
            }
        }

        private void ReopenClosedTab_Click(object sender, RoutedEventArgs e)
        {
            if (_closedSessions.Count == 0)
            {
                SetStatus("No recently closed document tabs to reopen.");
                return;
            }

            var session = _closedSessions.Pop();
            _sessions.Add(session);
            DocumentTabsList.SelectedItem = session;
            ActivateSession(session);
            UpdateClosedTabButtonsState();
            SetStatus($"Reopened tab '{session.FileName}'.");
        }

        private void UpdateClosedTabButtonsState()
        {
            bool hasClosed = _closedSessions.Count > 0;
            if (BtnReopenClosedTab != null)
            {
                BtnReopenClosedTab.Visibility = hasClosed ? Visibility.Visible : Visibility.Collapsed;
                BtnReopenClosedTab.Content = $"↺ Reopen Tab ({_closedSessions.Count})";
            }
            if (BtnEmptyReopenTab != null)
            {
                BtnEmptyReopenTab.Visibility = hasClosed ? Visibility.Visible : Visibility.Collapsed;
                BtnEmptyReopenTab.Content = $"↺ Reopen Closed Tab ({_closedSessions.Count})";
            }
        }

        #region Workspace Panel & Sidebar Tab Close / Reopen Management

        private void CloseLeftSidebarTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            string? tag = (sender as FrameworkElement)?.Tag as string;
            TabItem? targetTab = tag switch
            {
                "Pages" => LeftTabPages,
                "Outline" => LeftTabOutline,
                "Search" => LeftTabSearch,
                _ => null
            };

            if (targetTab != null)
            {
                targetTab.Visibility = Visibility.Collapsed;
                SyncLeftSidebarAfterTabVisibilityChange();
                SetStatus($"Closed '{tag}' tab. Click '{tag}' on the right of the tab bar or under 'Window & Tabs' to reopen it.");
            }
        }

        private void TogglePagesTab_Click(object sender, RoutedEventArgs e)
            => ToggleOrActivateLeftTab(LeftTabPages, "Pages");

        private void ToggleOutlineTab_Click(object sender, RoutedEventArgs e)
            => ToggleOrActivateLeftTab(LeftTabOutline, "Outline");

        private void ToggleSearchTab_Click(object sender, RoutedEventArgs e)
            => ToggleOrActivateLeftTab(LeftTabSearch, "Search");

        private void ToggleOrActivateLeftTab(TabItem tab, string name)
        {
            if (tab == null) return;

            if (tab.Visibility == Visibility.Visible && LeftSidebarBorder.Visibility == Visibility.Visible)
            {
                if (LeftSidebarTabs.SelectedItem == tab)
                {
                    tab.Visibility = Visibility.Collapsed;
                    SyncLeftSidebarAfterTabVisibilityChange();
                    SetStatus($"Closed '{name}' tab. Click '{name}' again to reopen.");
                }
                else
                {
                    LeftSidebarTabs.SelectedItem = tab;
                    UpdateWorkspacePanelToggleButtons();
                    SetStatus($"Switched to '{name}' tab.");
                }
            }
            else
            {
                tab.Visibility = Visibility.Visible;
                EnsureLeftSidebarExpanded();
                LeftSidebarTabs.SelectedItem = tab;
                UpdateWorkspacePanelToggleButtons();
                SetStatus($"Reopened '{name}' tab.");
            }
        }

        private void EnsureLeftSidebarExpanded()
        {
            LeftSidebarBorder.Visibility = Visibility.Visible;
            LeftGridSplitter.Visibility = Visibility.Visible;
            LeftSidebarColumn.MinWidth = 175;
            if (LeftSidebarColumn.Width.Value < 175)
            {
                LeftSidebarColumn.Width = new GridLength(250);
            }
            LeftSplitterColumn.Width = new GridLength(4);
        }

        private void SyncLeftSidebarAfterTabVisibilityChange()
        {
            TabItem[] allLeftTabs = { LeftTabPages, LeftTabOutline, LeftTabSearch };
            var firstVisible = allLeftTabs.FirstOrDefault(t => t != null && t.Visibility == Visibility.Visible);

            if (firstVisible == null)
            {
                LeftSidebarBorder.Visibility = Visibility.Collapsed;
                LeftGridSplitter.Visibility = Visibility.Collapsed;
                LeftSidebarColumn.MinWidth = 0;
                LeftSidebarColumn.Width = new GridLength(0);
                LeftSplitterColumn.Width = new GridLength(0);
            }
            else
            {
                EnsureLeftSidebarExpanded();
                if (LeftSidebarTabs.SelectedItem is not TabItem sel || sel.Visibility != Visibility.Visible)
                {
                    LeftSidebarTabs.SelectedItem = firstVisible;
                }
            }

            UpdateWorkspacePanelToggleButtons();
        }

        private void ToggleRightInspector_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = RightSidebarBorder.Visibility == Visibility.Visible;
            if (isVisible)
            {
                RightSidebarBorder.Visibility = Visibility.Collapsed;
                RightGridSplitter.Visibility = Visibility.Collapsed;
                RightSidebarColumn.MinWidth = 0;
                RightSidebarColumn.Width = new GridLength(0);
                RightSplitterColumn.Width = new GridLength(0);
                SetStatus("Closed Properties Inspector panel. Click '⚙ Inspector' on the tab bar to reopen.");
            }
            else
            {
                RightSidebarBorder.Visibility = Visibility.Visible;
                RightGridSplitter.Visibility = Visibility.Visible;
                RightSidebarColumn.MinWidth = 245;
                RightSidebarColumn.Width = new GridLength(315);
                RightSplitterColumn.Width = new GridLength(4);
                SetStatus("Reopened Properties Inspector panel.");
            }

            UpdateWorkspacePanelToggleButtons();
        }

        private void ToggleToolDock_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = ToolDockBorder.Visibility == Visibility.Visible;
            if (isVisible)
            {
                ToolDockBorder.Visibility = Visibility.Collapsed;
                ToolDockColumn.Width = new GridLength(0);
                SetStatus("Closed Left Tool Dock. Use 'Window & Tabs -> Toggle Left Tool Dock' or '🔄 Restore All' to reopen.");
            }
            else
            {
                ToolDockBorder.Visibility = Visibility.Visible;
                ToolDockColumn.Width = new GridLength(46);
                SetStatus("Reopened Left Tool Dock.");
            }
        }

        private void ToggleRibbon_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = RibbonHostBorder.Visibility == Visibility.Visible;
            RibbonHostBorder.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            UpdateWorkspacePanelToggleButtons();
            SetStatus(isVisible
                ? "Closed Top Ribbon toolbar. Click '🎀 Ribbon' on the tab bar to reopen."
                : "Reopened Top Ribbon toolbar.");
        }

        private void RestoreAllWorkspaceTabs_Click(object sender, RoutedEventArgs e)
        {
            LeftTabPages.Visibility = Visibility.Visible;
            LeftTabOutline.Visibility = Visibility.Visible;
            LeftTabSearch.Visibility = Visibility.Visible;
            EnsureLeftSidebarExpanded();
            LeftSidebarTabs.SelectedItem = LeftTabPages;

            RightSidebarBorder.Visibility = Visibility.Visible;
            RightGridSplitter.Visibility = Visibility.Visible;
            RightSidebarColumn.MinWidth = 245;
            RightSidebarColumn.Width = new GridLength(315);
            RightSplitterColumn.Width = new GridLength(4);

            ToolDockBorder.Visibility = Visibility.Visible;
            ToolDockColumn.Width = new GridLength(46);

            RibbonHostBorder.Visibility = Visibility.Visible;

            if (_sessions.Count == 0 && _closedSessions.Count > 0)
            {
                ReopenClosedTab_Click(this, e);
            }

            UpdateWorkspacePanelToggleButtons();
            SetStatus("Restored all workspace tabs, toolbars, and sidebars.");
        }

        private void UpdateWorkspacePanelToggleButtons()
        {
            static void StyleToggleButton(Button? btn, bool isOpen)
            {
                if (btn == null) return;
                btn.Background = isOpen
                    ? new SolidColorBrush(Color.FromRgb(37, 52, 74))
                    : new SolidColorBrush(Color.FromRgb(30, 34, 42));
                btn.BorderBrush = isOpen
                    ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                    : new SolidColorBrush(Color.FromRgb(54, 61, 73));
                btn.Foreground = isOpen
                    ? new SolidColorBrush(Color.FromRgb(226, 232, 240))
                    : new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }

            bool leftVisible = LeftSidebarBorder?.Visibility == Visibility.Visible;
            StyleToggleButton(BtnTogglePagesTab, leftVisible && LeftTabPages?.Visibility == Visibility.Visible);
            StyleToggleButton(BtnToggleOutlineTab, leftVisible && LeftTabOutline?.Visibility == Visibility.Visible);
            StyleToggleButton(BtnToggleSearchTab, leftVisible && LeftTabSearch?.Visibility == Visibility.Visible);
            StyleToggleButton(BtnToggleInspector, RightSidebarBorder?.Visibility == Visibility.Visible);
            StyleToggleButton(BtnToggleRibbon, RibbonHostBorder?.Visibility == Visibility.Visible);
        }

        #endregion

        #endregion

        #region Rendering PDF Page & Interactive WYSIWYG Overlay Canvas

        private void RenderCurrentPageAndOverlay()
        {
            if (_activeSession == null)
                return;

            int pageIdx = _activeSession.CurrentPageIndex;
            Size pageSizePt = _activeSession.PageSizesPt.TryGetValue(pageIdx, out var sz)
                ? sz
                : new Size(612, 792);

            PageStageGrid.Width = pageSizePt.Width;
            PageStageGrid.Height = pageSizePt.Height;
            OverlayCanvas.Width = pageSizePt.Width;
            OverlayCanvas.Height = pageSizePt.Height;

            PageScaleTransform.ScaleX = _activeSession.ZoomLevel;
            PageScaleTransform.ScaleY = _activeSession.ZoomLevel;

            int renderDpi = (int)Math.Clamp(110 * _activeSession.ZoomLevel, 96, 260);
            PdfPageImage.Source = _pdfService.RenderPageToBitmapSource(_activeSession.WorkingPdfBytes, pageIdx, renderDpi);

            PageIndicatorText.Text = $"Page {pageIdx + 1} of {_activeSession.PageCount}";
            ZoomPercentText.Text = $"{_activeSession.ZoomLevel * 100:F0}%";

            if (ThumbnailsListBox.SelectedIndex != pageIdx)
            {
                ThumbnailsListBox.SelectedIndex = pageIdx;
            }

            UpdateDocumentInfoPanel();
            RedrawOverlayCanvas();
        }

        private void RedrawOverlayCanvas()
        {
            OverlayCanvas.Children.Clear();
            if (_activeSession == null)
                return;

            int pageIdx = _activeSession.CurrentPageIndex;

            // 1. If in EditText mode or "Show Editable Text Frames" is checked, render detected PDF text blocks
            bool showTextFrames = _currentTool == EditorTool.EditText || ShowDetectedTextBlocksCheck.IsChecked == true;
            if (showTextFrames)
            {
                if (!_activeSession.PageTextBlocksCache.TryGetValue(pageIdx, out var blocks))
                {
                    blocks = _pdfService.ExtractPageTextBlocks(_activeSession.WorkingPdfBytes, pageIdx);
                    _activeSession.PageTextBlocksCache[pageIdx] = blocks;
                }

                foreach (var block in blocks)
                {
                    bool alreadyEdited = _activeSession.Annotations.Any(a =>
                        a.PageIndex == pageIdx &&
                        a.ToolType == EditorTool.EditText &&
                        Math.Abs(a.X - block.X) < 6 &&
                        Math.Abs(a.Y - block.Y) < 6);

                    if (alreadyEdited)
                        continue;

                    var frameBorder = new Border
                    {
                        Width = block.Width,
                        Height = block.Height,
                        Background = new SolidColorBrush(Color.FromArgb(18, 59, 130, 246)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(150, 37, 99, 235)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                        Cursor = Cursors.IBeam,
                        Tag = block,
                        ToolTip = $"Click to edit PDF text: \"{block.Text}\""
                    };

                    frameBorder.MouseLeftButtonDown += DetectedTextBlock_MouseLeftButtonDown;
                    Canvas.SetLeft(frameBorder, block.X);
                    Canvas.SetTop(frameBorder, block.Y);
                    OverlayCanvas.Children.Add(frameBorder);
                }
            }

            // 2. Render all search match highlights on this page + active match spotlight
            var pageMatches = _activeSession.SearchMatches
                .Where(m => m.PageNumber == pageIdx + 1)
                .ToList();

            foreach (var match in pageMatches)
            {
                bool isCurrent = ReferenceEquals(match, _highlightedSearchMatch);
                var searchHighlight = new Border
                {
                    Width = Math.Max(18, match.Width + (isCurrent ? 8 : 4)),
                    Height = Math.Max(14, match.Height + (isCurrent ? 6 : 2)),
                    Background = new SolidColorBrush(isCurrent
                        ? Color.FromArgb(145, 251, 146, 60)
                        : Color.FromArgb(95, 253, 224, 71)),
                    BorderBrush = new SolidColorBrush(isCurrent
                        ? Color.FromArgb(255, 220, 38, 38)
                        : Color.FromArgb(210, 217, 119, 6)),
                    BorderThickness = new Thickness(isCurrent ? 2.2 : 1.2),
                    CornerRadius = new CornerRadius(3),
                    IsHitTestVisible = false
                };

                Panel.SetZIndex(searchHighlight, isCurrent ? 45 : 15);
                Canvas.SetLeft(searchHighlight, Math.Max(0, match.X - (isCurrent ? 4 : 2)));
                Canvas.SetTop(searchHighlight, Math.Max(0, match.Y - (isCurrent ? 3 : 1)));
                OverlayCanvas.Children.Add(searchHighlight);
            }

            // 3. Render all annotations / edits on the current page
            var pageAnnotations = _activeSession.Annotations.Where(a => a.PageIndex == pageIdx).ToList();
            foreach (var item in pageAnnotations)
            {
                var visual = CreateVisualForAnnotation(item, item == _selectedAnnotation);
                OverlayCanvas.Children.Add(visual);
            }
        }

        private void DetectedTextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeSession == null || sender is not Border border || border.Tag is not DetectedPdfTextBlock block)
                return;

            e.Handled = true;
            _activeSession.PushUndoSnapshot();

            var editAnnotation = new PdfAnnotationItem
            {
                PageIndex = _activeSession.CurrentPageIndex,
                ToolType = EditorTool.EditText,
                X = block.X,
                Y = block.Y,
                Width = Math.Max(60, block.Width + 10),
                Height = Math.Max(16, block.Height + 4),
                Text = block.Text,
                OriginalText = block.Text,
                FontFamily = block.FontName,
                FontSize = Math.Max(8, block.FontSize),
                StrokeColorHex = "#1E293B",
                CoverBackgroundHex = "#FFFFFF",
                Opacity = 1.0
            };

            _activeSession.Annotations.Add(editAnnotation);
            SelectAnnotation(editAnnotation);
            RedrawOverlayCanvas();
            PropTextBox.Focus();
            PropTextBox.SelectAll();
            SetStatus($"Editing PDF text block in-place: \"{block.Text}\" — modify text on page or in the right panel.");
        }

        private FrameworkElement CreateVisualForAnnotation(PdfAnnotationItem item, bool isSelected)
        {
            Color strokeColor = ParseMediaColor(item.StrokeColorHex, item.Opacity);

            FrameworkElement contentElement;

            switch (item.ToolType)
            {
                case EditorTool.EditText:
                {
                    var tb = new TextBox
                    {
                        Text = item.Text,
                        FontFamily = new FontFamily(item.FontFamily),
                        FontSize = item.FontSize,
                        FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = item.IsItalic ? FontStyles.Italic : FontStyles.Normal,
                        Foreground = new SolidColorBrush(ParseMediaColor(item.StrokeColorHex, 1.0)),
                        Background = new SolidColorBrush(ParseMediaColor(item.CoverBackgroundHex, 1.0)),
                        BorderBrush = new SolidColorBrush(isSelected ? Color.FromRgb(229, 57, 53) : Color.FromRgb(59, 130, 246)),
                        BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                        Padding = new Thickness(1, 0, 1, 0),
                        MinWidth = Math.Max(40, item.Width),
                        MinHeight = Math.Max(16, item.Height),
                        AcceptsReturn = true
                    };
                    tb.GotFocus += (_, _) =>
                    {
                        if (_selectedAnnotation != item)
                        {
                            _selectedAnnotation = item;
                            SyncInspectorFromSelection();
                        }
                    };
                    tb.TextChanged += (_, _) =>
                    {
                        item.Text = tb.Text;
                        if (_activeSession != null) _activeSession.IsModified = true;
                        if (_selectedAnnotation == item && !_suppressInspectorEvents)
                        {
                            _suppressInspectorEvents = true;
                            PropTextBox.Text = item.Text;
                            _suppressInspectorEvents = false;
                        }
                    };
                    contentElement = tb;
                    break;
                }

                case EditorTool.AddText:
                {
                    Brush bgBrush = item.FillColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
                        ? Brushes.Transparent
                        : new SolidColorBrush(ParseMediaColor(item.FillColorHex, Math.Min(0.85, item.Opacity)));

                    var tb = new TextBox
                    {
                        Text = item.Text,
                        FontFamily = new FontFamily(item.FontFamily),
                        FontSize = item.FontSize,
                        FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = item.IsItalic ? FontStyles.Italic : FontStyles.Normal,
                        Foreground = new SolidColorBrush(strokeColor),
                        Background = bgBrush,
                        BorderBrush = new SolidColorBrush(isSelected ? Color.FromRgb(229, 57, 53) : Color.FromArgb(140, 100, 116, 139)),
                        BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                        Padding = new Thickness(4, 2, 4, 2),
                        MinWidth = Math.Max(80, item.Width),
                        MinHeight = Math.Max(24, item.Height),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap
                    };
                    tb.GotFocus += (_, _) =>
                    {
                        if (_selectedAnnotation != item)
                        {
                            _selectedAnnotation = item;
                            SyncInspectorFromSelection();
                        }
                    };
                    tb.TextChanged += (_, _) =>
                    {
                        item.Text = tb.Text;
                        if (_activeSession != null) _activeSession.IsModified = true;
                        if (_selectedAnnotation == item && !_suppressInspectorEvents)
                        {
                            _suppressInspectorEvents = true;
                            PropTextBox.Text = item.Text;
                            _suppressInspectorEvents = false;
                        }
                    };
                    contentElement = tb;
                    break;
                }

                case EditorTool.Highlight:
                {
                    contentElement = new Border
                    {
                        Width = Math.Max(8, item.Width),
                        Height = Math.Max(8, item.Height),
                        Background = new SolidColorBrush(ParseMediaColor(item.FillColorHex, 0.40)),
                        CornerRadius = new CornerRadius(2)
                    };
                    break;
                }

                case EditorTool.Underline:
                {
                    var grid = new Grid { Width = Math.Max(8, item.Width), Height = Math.Max(10, item.Height), Background = Brushes.Transparent };
                    grid.Children.Add(new Line
                    {
                        X1 = 0,
                        Y1 = Math.Max(10, item.Height) - 1,
                        X2 = Math.Max(8, item.Width),
                        Y2 = Math.Max(10, item.Height) - 1,
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = Math.Max(1.5, item.StrokeThickness)
                    });
                    contentElement = grid;
                    break;
                }

                case EditorTool.Strikethrough:
                {
                    double h = Math.Max(10, item.Height);
                    var grid = new Grid { Width = Math.Max(8, item.Width), Height = h, Background = Brushes.Transparent };
                    grid.Children.Add(new Line
                    {
                        X1 = 0,
                        Y1 = h / 2.0,
                        X2 = Math.Max(8, item.Width),
                        Y2 = h / 2.0,
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = Math.Max(1.5, item.StrokeThickness)
                    });
                    contentElement = grid;
                    break;
                }

                case EditorTool.StickyNote:
                {
                    var notePanel = new StackPanel();
                    notePanel.Children.Add(new TextBlock
                    {
                        Text = $"💬 Note ({item.Author})",
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(120, 53, 15))
                    });
                    notePanel.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        FontSize = 10.5,
                        Foreground = Brushes.Black,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });

                    contentElement = new Border
                    {
                        Width = Math.Max(135, item.Width),
                        MinHeight = Math.Max(48, item.Height),
                        Background = new SolidColorBrush(ParseMediaColor(item.FillColorHex, 0.95)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(202, 138, 4)),
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(7, 5, 7, 5),
                        Child = notePanel
                    };
                    break;
                }

                case EditorTool.Freehand:
                {
                    var poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = item.StrokeThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    foreach (var pt in item.Points)
                    {
                        poly.Points.Add(pt);
                    }
                    poly.Tag = item;
                    poly.MouseLeftButtonDown += AnnotationVisual_MouseLeftButtonDown;
                    return poly;
                }

                case EditorTool.Rectangle:
                {
                    contentElement = new Border
                    {
                        Width = Math.Max(6, item.Width),
                        Height = Math.Max(6, item.Height),
                        BorderBrush = new SolidColorBrush(strokeColor),
                        BorderThickness = new Thickness(item.StrokeThickness),
                        Background = Brushes.Transparent
                    };
                    break;
                }

                case EditorTool.Ellipse:
                {
                    contentElement = new Ellipse
                    {
                        Width = Math.Max(6, item.Width),
                        Height = Math.Max(6, item.Height),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = item.StrokeThickness,
                        Fill = Brushes.Transparent
                    };
                    break;
                }

                case EditorTool.Line:
                {
                    double x1 = item.Points.Count >= 2 ? item.Points[0].X : item.X;
                    double y1 = item.Points.Count >= 2 ? item.Points[0].Y : item.Y;
                    double x2 = item.Points.Count >= 2 ? item.Points[^1].X : item.X + item.Width;
                    double y2 = item.Points.Count >= 2 ? item.Points[^1].Y : item.Y + item.Height;

                    var canvas = new Canvas();
                    var mainLine = new Line
                    {
                        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = item.StrokeThickness
                    };
                    canvas.Children.Add(mainLine);

                    double angle = Math.Atan2(y2 - y1, x2 - x1);
                    double arrowLen = 9 + item.StrokeThickness;
                    canvas.Children.Add(new Line
                    {
                        X1 = x2, Y1 = y2,
                        X2 = x2 - arrowLen * Math.Cos(angle - Math.PI / 6),
                        Y2 = y2 - arrowLen * Math.Sin(angle - Math.PI / 6),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = item.StrokeThickness
                    });
                    canvas.Children.Add(new Line
                    {
                        X1 = x2, Y1 = y2,
                        X2 = x2 - arrowLen * Math.Cos(angle + Math.PI / 6),
                        Y2 = y2 - arrowLen * Math.Sin(angle + Math.PI / 6),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = item.StrokeThickness
                    });

                    canvas.Tag = item;
                    canvas.MouseLeftButtonDown += AnnotationVisual_MouseLeftButtonDown;
                    return canvas;
                }

                case EditorTool.Stamp:
                {
                    string label = string.IsNullOrWhiteSpace(item.StampLabel) ? item.Text : item.StampLabel;
                    var stampColor = ParseMediaColor(item.StrokeColorHex, 0.90);
                    var bgColor = ParseMediaColor(item.StrokeColorHex, 0.14);

                    contentElement = new Border
                    {
                        Width = Math.Max(110, item.Width),
                        Height = Math.Max(36, item.Height),
                        Background = new SolidColorBrush(bgColor),
                        BorderBrush = new SolidColorBrush(stampColor),
                        BorderThickness = new Thickness(2.5),
                        CornerRadius = new CornerRadius(6),
                        Child = new TextBlock
                        {
                            Text = label,
                            Foreground = new SolidColorBrush(stampColor),
                            FontWeight = FontWeights.ExtraBold,
                            FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    break;
                }

                case EditorTool.Signature:
                case EditorTool.InsertImage:
                {
                    BitmapSource? bmp = null;
                    if (item.ImageBytes != null && item.ImageBytes.Length > 0)
                    {
                        try
                        {
                            using var ms = new MemoryStream(item.ImageBytes);
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.StreamSource = ms;
                            bi.EndInit();
                            bi.Freeze();
                            bmp = bi;
                        }
                        catch
                        {
                            bmp = null;
                        }
                    }

                    if (bmp != null)
                    {
                        contentElement = new Image
                        {
                            Source = bmp,
                            Width = Math.Max(40, item.Width),
                            Height = Math.Max(25, item.Height),
                            Opacity = item.Opacity,
                            Stretch = Stretch.Fill
                        };
                    }
                    else
                    {
                        contentElement = new TextBlock
                        {
                            Text = item.Text,
                            FontFamily = new FontFamily("Times New Roman"),
                            FontStyle = FontStyles.Italic,
                            FontWeight = FontWeights.Bold,
                            FontSize = 24,
                            Foreground = new SolidColorBrush(strokeColor)
                        };
                    }
                    break;
                }

                case EditorTool.Redact:
                {
                    contentElement = new Border
                    {
                        Width = Math.Max(12, item.Width),
                        Height = Math.Max(12, item.Height),
                        Background = Brushes.Black,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                        BorderThickness = new Thickness(1.5),
                        Child = new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(item.Text) ? "REDACTED" : item.Text,
                            Foreground = Brushes.White,
                            FontSize = 8.5,
                            FontWeight = FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    break;
                }

                default:
                    contentElement = new Border { Width = item.Width, Height = item.Height };
                    break;
            }

            var wrapper = new Border
            {
                Child = contentElement,
                Background = Brushes.Transparent,
                BorderBrush = isSelected ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) : Brushes.Transparent,
                BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(0),
                Padding = new Thickness(1),
                Cursor = _currentTool == EditorTool.Select ? Cursors.SizeAll : Cursors.Arrow,
                Tag = item
            };

            wrapper.MouseLeftButtonDown += AnnotationVisual_MouseLeftButtonDown;

            if (!isSelected)
            {
                Canvas.SetLeft(wrapper, item.X);
                Canvas.SetTop(wrapper, item.Y);
                return wrapper;
            }

            var container = new Grid
            {
                Tag = item
            };
            container.Children.Add(wrapper);

            if (item.ToolType != EditorTool.Freehand && item.ToolType != EditorTool.Line)
            {
                container.Children.Add(CreateResizeHandle(item, "NW", HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE));
                container.Children.Add(CreateResizeHandle(item, "NE", HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW));
                container.Children.Add(CreateResizeHandle(item, "SW", HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW));
                container.Children.Add(CreateResizeHandle(item, "SE", HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE));
                container.Children.Add(CreateResizeHandle(item, "E", HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE));
                container.Children.Add(CreateResizeHandle(item, "S", HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS));
            }

            Canvas.SetLeft(container, item.X);
            Canvas.SetTop(container, item.Y);
            return container;
        }

        private Border CreateResizeHandle(
            PdfAnnotationItem item,
            string handleDir,
            HorizontalAlignment hAlign,
            VerticalAlignment vAlign,
            Cursor cursor)
        {
            var handle = new Border
            {
                Width = 9,
                Height = 9,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Margin = new Thickness(-4.5),
                Cursor = cursor,
                Tag = handleDir,
                ToolTip = "Drag to resize (Hold Shift to lock aspect ratio)"
            };

            handle.MouseLeftButtonDown += (s, e) =>
            {
                SelectAnnotation(item);
                _isResizingExistingItem = true;
                _activeResizeHandle = handleDir;
                _mouseDownCanvasPt = e.GetPosition(OverlayCanvas);
                _resizeOriginalBounds = new Rect(item.X, item.Y, Math.Max(20, item.Width), Math.Max(16, item.Height));
                _activeSession?.PushUndoSnapshot();
                OverlayCanvas.CaptureMouse();
                e.Handled = true;
            };

            return handle;
        }

        private void AnnotationVisual_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfAnnotationItem item)
            {
                SelectAnnotation(item);

                if (_currentTool == EditorTool.Select || _currentTool == EditorTool.EditText || _currentTool == EditorTool.AddText)
                {
                    _isDraggingExistingItem = true;
                    _mouseDownCanvasPt = e.GetPosition(OverlayCanvas);
                    _dragItemOriginalPos = new Point(item.X, item.Y);
                    _activeSession?.PushUndoSnapshot();
                    OverlayCanvas.CaptureMouse();
                    e.Handled = true;
                }
            }
        }

        #endregion

        #region Canvas Mouse Interaction (Draw, Highlight, Edit Text, Pan, Move, Resize)

        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeSession == null)
                return;

            var pt = e.GetPosition(OverlayCanvas);
            _mouseDownCanvasPt = pt;

            if (_currentTool == EditorTool.Pan)
            {
                _isPanning = true;
                _panStartMouseWindowPt = e.GetPosition(this);
                _panStartScrollOffset = new Point(PageScrollViewer.HorizontalOffset, PageScrollViewer.VerticalOffset);
                OverlayCanvas.CaptureMouse();
                return;
            }

            if (_currentTool == EditorTool.Select)
            {
                SelectAnnotation(null);
                RedrawOverlayCanvas();
                return;
            }

            if (_currentTool == EditorTool.EditText)
            {
                CreateSingleClickAnnotation(EditorTool.AddText, pt);
                return;
            }

            if (_currentTool == EditorTool.AddText || _currentTool == EditorTool.StickyNote || _currentTool == EditorTool.Stamp)
            {
                CreateSingleClickAnnotation(_currentTool, pt);
                return;
            }

            _activeSession.PushUndoSnapshot();
            _isDraggingNewShape = true;

            string strokeHex = GetSelectedStrokeColorHex();
            string fillHex = GetSelectedFillColorHex();

            _activeDrawingAnnotation = new PdfAnnotationItem
            {
                PageIndex = _activeSession.CurrentPageIndex,
                ToolType = _currentTool,
                X = pt.X,
                Y = pt.Y,
                Width = 4,
                Height = 4,
                StrokeColorHex = strokeHex,
                FillColorHex = fillHex,
                StrokeThickness = PropThicknessSlider.Value,
                Opacity = _currentTool == EditorTool.Highlight ? 0.40 : PropOpacitySlider.Value,
                Text = _currentTool == EditorTool.Redact ? "REDACTED" : string.Empty
            };

            if (_currentTool == EditorTool.Freehand || _currentTool == EditorTool.Line)
            {
                _activeDrawingAnnotation.Points.Add(pt);
            }

            _activeSession.Annotations.Add(_activeDrawingAnnotation);
            SelectAnnotation(_activeDrawingAnnotation);
            OverlayCanvas.CaptureMouse();
            RedrawOverlayCanvas();
        }

        private void CreateSingleClickAnnotation(EditorTool tool, Point pt)
        {
            if (_activeSession == null)
                return;

            _activeSession.PushUndoSnapshot();
            string strokeHex = GetSelectedStrokeColorHex();
            string fillHex = GetSelectedFillColorHex();

            var item = new PdfAnnotationItem
            {
                PageIndex = _activeSession.CurrentPageIndex,
                ToolType = tool,
                X = pt.X,
                Y = pt.Y,
                FontFamily = GetSelectedFontFamily(),
                FontSize = GetSelectedFontSize(),
                IsBold = PropBoldCheck.IsChecked == true,
                IsItalic = PropItalicCheck.IsChecked == true,
                StrokeColorHex = strokeHex,
                FillColorHex = fillHex,
                Opacity = PropOpacitySlider.Value,
                StrokeThickness = PropThicknessSlider.Value
            };

            if (tool == EditorTool.AddText)
            {
                item.Width = 170;
                item.Height = 28;
                item.Text = "New Text Box";
                item.FillColorHex = "Transparent";
            }
            else if (tool == EditorTool.StickyNote)
            {
                item.Width = 165;
                item.Height = 58;
                item.FillColorHex = "#FEF08A";
                item.Text = "Review comment: Please verify this section.";
            }
            else if (tool == EditorTool.Stamp)
            {
                string stampLabel = (StampPresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "APPROVED";
                item.StampLabel = stampLabel;
                item.Text = stampLabel;
                item.Width = 140;
                item.Height = 40;
                item.StrokeColorHex = stampLabel switch
                {
                    "APPROVED" or "FINAL" => "#15803D",
                    "CONFIDENTIAL" or "VOID" or "URGENT" => "#D32F2F",
                    _ => "#1D4ED8"
                };
            }

            _activeSession.Annotations.Add(item);
            SelectAnnotation(item);
            RedrawOverlayCanvas();
            PropTextBox.Focus();
            PropTextBox.SelectAll();
        }

        private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pt = e.GetPosition(OverlayCanvas);
            CursorCoordsText.Text = $"X: {pt.X:F0} pt, Y: {pt.Y:F0} pt";

            if (_isPanning)
            {
                var winPt = e.GetPosition(this);
                double dx = winPt.X - _panStartMouseWindowPt.X;
                double dy = winPt.Y - _panStartMouseWindowPt.Y;
                PageScrollViewer.ScrollToHorizontalOffset(_panStartScrollOffset.X - dx);
                PageScrollViewer.ScrollToVerticalOffset(_panStartScrollOffset.Y - dy);
                return;
            }

            if (_isResizingExistingItem && _selectedAnnotation != null)
            {
                double dx = pt.X - _mouseDownCanvasPt.X;
                double dy = pt.Y - _mouseDownCanvasPt.Y;
                double newX = _resizeOriginalBounds.X;
                double newY = _resizeOriginalBounds.Y;
                double newW = _resizeOriginalBounds.Width;
                double newH = _resizeOriginalBounds.Height;

                if (_activeResizeHandle.Contains('E'))
                    newW = Math.Max(20, _resizeOriginalBounds.Width + dx);
                if (_activeResizeHandle.Contains('S'))
                    newH = Math.Max(16, _resizeOriginalBounds.Height + dy);
                if (_activeResizeHandle.Contains('W'))
                {
                    double maxDx = _resizeOriginalBounds.Width - 20;
                    double appliedDx = Math.Min(dx, maxDx);
                    newX = Math.Max(0, _resizeOriginalBounds.X + appliedDx);
                    newW = Math.Max(20, _resizeOriginalBounds.Right - newX);
                }
                if (_activeResizeHandle.Contains('N'))
                {
                    double maxDy = _resizeOriginalBounds.Height - 16;
                    double appliedDy = Math.Min(dy, maxDy);
                    newY = Math.Max(0, _resizeOriginalBounds.Y + appliedDy);
                    newH = Math.Max(16, _resizeOriginalBounds.Bottom - newY);
                }

                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && _resizeOriginalBounds.Height > 0)
                {
                    double aspect = _resizeOriginalBounds.Width / _resizeOriginalBounds.Height;
                    newH = Math.Max(16, newW / aspect);
                }

                _selectedAnnotation.X = newX;
                _selectedAnnotation.Y = newY;
                _selectedAnnotation.Width = Math.Round(newW, 1);
                _selectedAnnotation.Height = Math.Round(newH, 1);

                _suppressInspectorEvents = true;
                PropWidthBox.Text = _selectedAnnotation.Width.ToString("F0");
                PropHeightBox.Text = _selectedAnnotation.Height.ToString("F0");
                _suppressInspectorEvents = false;

                RedrawOverlayCanvas();
                return;
            }

            if (_isDraggingExistingItem && _selectedAnnotation != null)
            {
                double dx = pt.X - _mouseDownCanvasPt.X;
                double dy = pt.Y - _mouseDownCanvasPt.Y;
                _selectedAnnotation.X = Math.Max(0, _dragItemOriginalPos.X + dx);
                _selectedAnnotation.Y = Math.Max(0, _dragItemOriginalPos.Y + dy);
                RedrawOverlayCanvas();
                return;
            }

            if (_isDraggingNewShape && _activeDrawingAnnotation != null)
            {
                if (_activeDrawingAnnotation.ToolType == EditorTool.Freehand)
                {
                    _activeDrawingAnnotation.Points.Add(pt);
                }
                else if (_activeDrawingAnnotation.ToolType == EditorTool.Line)
                {
                    if (_activeDrawingAnnotation.Points.Count == 1)
                        _activeDrawingAnnotation.Points.Add(pt);
                    else
                        _activeDrawingAnnotation.Points[^1] = pt;

                    _activeDrawingAnnotation.Width = pt.X - _mouseDownCanvasPt.X;
                    _activeDrawingAnnotation.Height = pt.Y - _mouseDownCanvasPt.Y;
                }
                else
                {
                    double minX = Math.Min(_mouseDownCanvasPt.X, pt.X);
                    double minY = Math.Min(_mouseDownCanvasPt.Y, pt.Y);
                    double w = Math.Max(8, Math.Abs(pt.X - _mouseDownCanvasPt.X));
                    double h = Math.Max(8, Math.Abs(pt.Y - _mouseDownCanvasPt.Y));

                    _activeDrawingAnnotation.X = minX;
                    _activeDrawingAnnotation.Y = minY;
                    _activeDrawingAnnotation.Width = w;
                    _activeDrawingAnnotation.Height = h;
                }

                RedrawOverlayCanvas();
            }
        }

        private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                OverlayCanvas.ReleaseMouseCapture();
            }

            if (_isResizingExistingItem)
            {
                _isResizingExistingItem = false;
                OverlayCanvas.ReleaseMouseCapture();
                if (_activeSession != null)
                {
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                    if (_selectedAnnotation != null)
                    {
                        SetStatus($"Resized {_selectedAnnotation.ToolDisplayName} to {_selectedAnnotation.Width:F0} × {_selectedAnnotation.Height:F0} pt.");
                    }
                }
            }

            if (_isDraggingExistingItem)
            {
                _isDraggingExistingItem = false;
                OverlayCanvas.ReleaseMouseCapture();
                if (_activeSession != null)
                {
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                }
            }

            if (_isDraggingNewShape)
            {
                _isDraggingNewShape = false;
                _activeDrawingAnnotation = null;
                OverlayCanvas.ReleaseMouseCapture();
                if (_activeSession != null)
                {
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                }
            }
        }

        #endregion

        #region Tool Selection & Inspector Synchronization

        private void SelectTool_Click(object sender, RoutedEventArgs e)
        {
            string? tagName = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrWhiteSpace(tagName) && Enum.TryParse<EditorTool>(tagName, out var tool))
            {
                _currentTool = tool;
                UpdateToolButtonsVisualState();
                RedrawOverlayCanvas();

                ModeBanner.Visibility = (_currentTool == EditorTool.EditText || _currentTool == EditorTool.Redact)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (_currentTool == EditorTool.EditText)
                {
                    ModeBannerText.Text = "✏ Edit PDF Text Mode: Click any dashed blue text block on the page to edit or replace its text in-place.";
                }
                else if (_currentTool == EditorTool.Redact)
                {
                    ModeBannerText.Text = "⬛ Redaction Mode: Drag a box over sensitive text or graphics to black out, then click 'Apply & Flatten'.";
                }

                SetStatus($"Active Tool: {_currentTool}");
            }
        }

        private void UpdateToolButtonsVisualState()
        {
            Button[] toolButtons =
            {
                BtnToolSelect, BtnToolPan, BtnToolEditText, BtnToolAddText,
                BtnToolHighlight, BtnToolUnderline, BtnToolStrike, BtnToolSticky,
                BtnToolFreehand, BtnToolRect, BtnToolEllipse, BtnToolLine,
                BtnToolStamp, BtnToolRedact,
                DockBtnSelect, DockBtnPan, DockBtnEditText, DockBtnAddText,
                DockBtnHighlight, DockBtnUnderline, DockBtnStrike, DockBtnSticky,
                DockBtnFreehand, DockBtnRect, DockBtnEllipse, DockBtnLine,
                DockBtnStamp, DockBtnRedact
            };

            foreach (var btn in toolButtons)
            {
                if (btn == null) continue;
                bool isActive = btn.Tag?.ToString() == _currentTool.ToString();
                btn.Background = isActive
                    ? new SolidColorBrush(Color.FromRgb(229, 57, 53))
                    : new SolidColorBrush(Color.FromRgb(38, 43, 51));
                btn.BorderBrush = isActive
                    ? new SolidColorBrush(Color.FromRgb(248, 113, 113))
                    : new SolidColorBrush(Color.FromRgb(54, 61, 73));
            }
        }

        private void SelectAnnotation(PdfAnnotationItem? item)
        {
            _selectedAnnotation = item;
            AnnotationsListBox.SelectedItem = item;
            SyncInspectorFromSelection();
        }

        private void SyncInspectorFromSelection()
        {
            _suppressInspectorEvents = true;
            try
            {
                if (_selectedAnnotation == null)
                {
                    SelectedElementTypeLabel.Text = $"Tool Defaults ({_currentTool})";
                    PropTextBox.Text = string.Empty;
                    if (PropWidthBox != null) PropWidthBox.Text = "180";
                    if (PropHeightBox != null) PropHeightBox.Text = "120";
                    return;
                }

                SelectedElementTypeLabel.Text = $"Selected: {_selectedAnnotation.ToolDisplayName} ({_selectedAnnotation.PageDisplay})";
                PropTextBox.Text = _selectedAnnotation.Text;
                if (PropWidthBox != null) PropWidthBox.Text = _selectedAnnotation.Width.ToString("F0");
                if (PropHeightBox != null) PropHeightBox.Text = _selectedAnnotation.Height.ToString("F0");
                PropFontSizeBox.Text = _selectedAnnotation.FontSize.ToString("F0");
                PropBoldCheck.IsChecked = _selectedAnnotation.IsBold;
                PropItalicCheck.IsChecked = _selectedAnnotation.IsItalic;
                PropThicknessSlider.Value = _selectedAnnotation.StrokeThickness;
                PropOpacitySlider.Value = _selectedAnnotation.Opacity;

                for (int i = 0; i < PropFontFamilyCombo.Items.Count; i++)
                {
                    if ((PropFontFamilyCombo.Items[i] as ComboBoxItem)?.Content?.ToString() == _selectedAnnotation.FontFamily)
                    {
                        PropFontFamilyCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally
            {
                _suppressInspectorEvents = false;
            }
        }

        private void PropSizeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            bool changed = false;
            if (PropWidthBox != null && double.TryParse(PropWidthBox.Text, out double w))
            {
                _selectedAnnotation.Width = Math.Clamp(w, 16, 1200);
                changed = true;
            }
            if (PropHeightBox != null && double.TryParse(PropHeightBox.Text, out double h))
            {
                _selectedAnnotation.Height = Math.Clamp(h, 12, 1200);
                changed = true;
            }

            if (changed)
            {
                if (_activeSession != null)
                {
                    _activeSession.IsModified = true;
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                }
                RedrawOverlayCanvas();
            }
        }

        private void ScaleUpSelected_Click(object sender, RoutedEventArgs e)
            => ScaleSelectedAnnotation(1.15);

        private void ScaleDownSelected_Click(object sender, RoutedEventArgs e)
            => ScaleSelectedAnnotation(1.0 / 1.15);

        private void ScaleSelectedAnnotation(double factor)
        {
            if (_activeSession == null || _selectedAnnotation == null)
                return;

            _activeSession.PushUndoSnapshot();
            _selectedAnnotation.Width = Math.Clamp(Math.Round(_selectedAnnotation.Width * factor, 1), 20, 1200);
            _selectedAnnotation.Height = Math.Clamp(Math.Round(_selectedAnnotation.Height * factor, 1), 16, 1200);
            SyncInspectorFromSelection();
            RedrawOverlayCanvas();
            RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
            SetStatus($"Resized {_selectedAnnotation.ToolDisplayName} to {_selectedAnnotation.Width:F0} × {_selectedAnnotation.Height:F0} pt.");
        }

        private void PropTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            _selectedAnnotation.Text = PropTextBox.Text;
            if (_selectedAnnotation.ToolType == EditorTool.Stamp)
            {
                _selectedAnnotation.StampLabel = PropTextBox.Text;
            }
            if (_activeSession != null) _activeSession.IsModified = true;
            RedrawOverlayCanvas();
        }

        private void PropFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            _selectedAnnotation.FontFamily = GetSelectedFontFamily();
            if (_activeSession != null) _activeSession.IsModified = true;
            RedrawOverlayCanvas();
        }

        private void PropFontSizeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            if (double.TryParse(PropFontSizeBox.Text, out double sz))
            {
                _selectedAnnotation.FontSize = Math.Clamp(sz, 6, 120);
                if (_activeSession != null) _activeSession.IsModified = true;
                RedrawOverlayCanvas();
            }
        }

        private void PropStyleCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            _selectedAnnotation.IsBold = PropBoldCheck.IsChecked == true;
            _selectedAnnotation.IsItalic = PropItalicCheck.IsChecked == true;
            if (_activeSession != null) _activeSession.IsModified = true;
            RedrawOverlayCanvas();
        }

        private void PropColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            _selectedAnnotation.StrokeColorHex = GetSelectedStrokeColorHex();
            _selectedAnnotation.FillColorHex = GetSelectedFillColorHex();
            if (_activeSession != null) _activeSession.IsModified = true;
            RedrawOverlayCanvas();
        }

        private void PropSliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressInspectorEvents || _selectedAnnotation == null)
                return;

            _selectedAnnotation.StrokeThickness = PropThicknessSlider.Value;
            _selectedAnnotation.Opacity = PropOpacitySlider.Value;
            if (_activeSession != null) _activeSession.IsModified = true;
            RedrawOverlayCanvas();
        }

        private string GetSelectedFontFamily()
            => (PropFontFamilyCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Arial";

        private double GetSelectedFontSize()
            => double.TryParse(PropFontSizeBox?.Text, out double sz) ? Math.Clamp(sz, 6, 120) : 12.0;

        private string GetSelectedStrokeColorHex()
            => (PropStrokeColorCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#E53935";

        private string GetSelectedFillColorHex()
            => (PropFillColorCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFEB3B";

        #endregion

        #region File Open, Save, Export & Flatten Actions

        private void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open PDF Document",
                Filter = "PDF Documents (*.pdf)|*.pdf|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        var session = new PdfDocumentSession
                        {
                            FilePath = file,
                            FileName = System.IO.Path.GetFileName(file),
                            WorkingPdfBytes = bytes,
                            ZoomLevel = 1.25
                        };
                        InitializeAndActivateSession(session);
                        SetStatus($"Opened '{session.FileName}' ({session.PageCount} pages).");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open PDF '{file}':\n{ex.Message}", "Open PDF Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void NewSamplePdf_Click(object sender, RoutedEventArgs e)
        {
            CreateAndLoadSampleDocument();
        }

        private void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            SaveSessionToFile(_activeSession, forceDialog: false);
        }

        private void SavePdfAs_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            SaveSessionToFile(_activeSession, forceDialog: true);
        }

        private void SaveSessionToFile(PdfDocumentSession session, bool forceDialog)
        {
            string? targetPath = session.FilePath;
            if (forceDialog || string.IsNullOrWhiteSpace(targetPath))
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Save PDF Document",
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = session.FileName
                };
                if (sfd.ShowDialog() != true)
                    return;

                targetPath = sfd.FileName;
            }

            try
            {
                byte[] finalBytes = _pdfService.BakeAnnotationsIntoPdf(session.WorkingPdfBytes, session.Annotations);
                File.WriteAllBytes(targetPath, finalBytes);

                session.WorkingPdfBytes = finalBytes;
                session.Annotations.Clear();
                session.FilePath = targetPath;
                session.IsModified = false;

                _pdfService.PopulateDocumentMetadata(session);
                RebuildSessionThumbnails(session);
                RenderCurrentPageAndOverlay();
                SetStatus($"Saved PDF with all edits to '{targetPath}'.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save PDF:\n{ex.Message}", "Save PDF Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FlattenAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            if (_activeSession.Annotations.Count == 0)
            {
                SetStatus("No pending overlay edits to flatten.");
                return;
            }

            try
            {
                int count = _activeSession.Annotations.Count;
                _activeSession.PushUndoSnapshot();
                _activeSession.WorkingPdfBytes = _pdfService.BakeAnnotationsIntoPdf(_activeSession.WorkingPdfBytes, _activeSession.Annotations);
                _activeSession.Annotations.Clear();
                _activeSession.PageTextBlocksCache.Clear();
                _activeSession.IsModified = true;
                _selectedAnnotation = null;

                _pdfService.PopulateDocumentMetadata(_activeSession);
                RebuildSessionThumbnails(_activeSession);
                RenderCurrentPageAndOverlay();
                SetStatus($"Flattened and baked {count} edit(s)/annotation(s) permanently into the PDF stream (Ctrl+Z to Undo).");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error flattening annotations:\n{ex.Message}", "Flatten Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportPng_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            var sfd = new SaveFileDialog
            {
                Title = "Export Current Page as High-DPI PNG",
                Filter = "PNG Image (*.png)|*.png",
                FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_activeSession.FileName)}_Page{_activeSession.CurrentPageNumber}.png"
            };

            if (sfd.ShowDialog() == true)
            {
                byte[] baked = _pdfService.BakeAnnotationsIntoPdf(_activeSession.WorkingPdfBytes, _activeSession.Annotations);
                var bmp = _pdfService.RenderPageToBitmapSource(baked, _activeSession.CurrentPageIndex, dpi: 220);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(sfd.FileName);
                encoder.Save(fs);
                SetStatus($"Exported Page {_activeSession.CurrentPageNumber} to '{sfd.FileName}'.");
            }
        }

        private void ExportText_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            var sfd = new SaveFileDialog
            {
                Title = "Export Extracted PDF Text",
                Filter = "Text Document (*.txt)|*.txt",
                FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_activeSession.FileName)}_ExtractedText.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                byte[] baked = _pdfService.BakeAnnotationsIntoPdf(_activeSession.WorkingPdfBytes, _activeSession.Annotations);
                string text = _pdfService.ExtractFullDocumentText(baked);
                File.WriteAllText(sfd.FileName, text);
                SetStatus($"Exported full document text to '{sfd.FileName}'.");
            }
        }

        #endregion

        #region Fill & Sign, Insert Image, Watermark & Page Numbers

        private void OpenSignatureDialog_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            try
            {
                var dlg = new SignatureDialog { Owner = this };
                if (dlg.ShowDialog() == true && dlg.SignatureImageBytes != null)
                {
                    _activeSession.PushUndoSnapshot();
                    var sigItem = new PdfAnnotationItem
                    {
                        PageIndex = _activeSession.CurrentPageIndex,
                        ToolType = EditorTool.Signature,
                        X = 110,
                        Y = 290,
                        Width = 175,
                        Height = 56,
                        ImageBytes = dlg.SignatureImageBytes,
                        Text = dlg.SignatureText,
                        StrokeColorHex = dlg.SelectedInkHex
                    };

                    _activeSession.Annotations.Add(sigItem);
                    _currentTool = EditorTool.Select;
                    UpdateToolButtonsVisualState();
                    SelectAnnotation(sigItem);
                    RedrawOverlayCanvas();
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                    SetStatus("Signature placed on page! Drag to position or use corner handles to resize.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to place signature:\n{ex.Message}", "Fill & Sign Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertImageTool_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            var ofd = new OpenFileDialog
            {
                Title = "Select Image to Insert into PDF",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
            };

            if (ofd.ShowDialog() == true)
            {
                try
                {
                    byte[] imgBytes = File.ReadAllBytes(ofd.FileName);
                    double width = 180;
                    double height = 120;
                    try
                    {
                        using var ms = new MemoryStream(imgBytes);
                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames[0];
                        if (frame.PixelWidth > 0 && frame.PixelHeight > 0)
                        {
                            double scale = Math.Min(200.0 / frame.PixelWidth, 160.0 / frame.PixelHeight);
                            scale = Math.Min(1.0, scale);
                            width = Math.Max(48, Math.Round(frame.PixelWidth * scale));
                            height = Math.Max(32, Math.Round(frame.PixelHeight * scale));
                        }
                    }
                    catch
                    {
                        // Use default dimensions if metadata read fails
                    }

                    _activeSession.PushUndoSnapshot();

                    var imgItem = new PdfAnnotationItem
                    {
                        PageIndex = _activeSession.CurrentPageIndex,
                        ToolType = EditorTool.InsertImage,
                        X = 120,
                        Y = 180,
                        Width = width,
                        Height = height,
                        ImageBytes = imgBytes,
                        Text = System.IO.Path.GetFileName(ofd.FileName)
                    };

                    _activeSession.Annotations.Add(imgItem);
                    _currentTool = EditorTool.Select;
                    UpdateToolButtonsVisualState();
                    SelectAnnotation(imgItem);
                    RedrawOverlayCanvas();
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                    SetStatus($"Inserted image '{imgItem.Text}' on Page {_activeSession.CurrentPageNumber}. Drag handles to resize.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to insert image:\n{ex.Message}", "Add Image Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OpenWatermarkDialog_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            var dlg = new WatermarkDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _activeSession.PushUndoSnapshot();
                _activeSession.WorkingPdfBytes = _pdfService.ApplyWatermark(
                    _activeSession.WorkingPdfBytes,
                    dlg.WatermarkText,
                    dlg.WatermarkFontSize,
                    dlg.WatermarkColorHex,
                    dlg.WatermarkOpacity,
                    dlg.WatermarkAngle,
                    dlg.ApplyToAllPages,
                    _activeSession.CurrentPageIndex);

                _activeSession.IsModified = true;
                _activeSession.PageTextBlocksCache.Clear();
                RebuildSessionThumbnails(_activeSession);
                RenderCurrentPageAndOverlay();
                SetStatus($"Applied watermark \"{dlg.WatermarkText}\" ({(dlg.ApplyToAllPages ? "All Pages" : "Current Page")}). Press Ctrl+Z or Undo to revert.");
            }
        }

        private void AddPageNumbers_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            _activeSession.PushUndoSnapshot();
            _activeSession.WorkingPdfBytes = _pdfService.ApplyHeaderFooterPageNumbers(
                _activeSession.WorkingPdfBytes,
                headerLeft: _activeSession.FileName,
                headerRight: DateTime.Now.ToString("yyyy-MM-dd"),
                includePageNumbers: true);

            _activeSession.IsModified = true;
            _activeSession.PageTextBlocksCache.Clear();
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus("Applied running header and 'Page X of Y' Bates/page numbering across all pages (Ctrl+Z to Undo).");
        }

        #endregion

        #region Organize Pages (Rotate, Blank Page, Duplicate, Move, Delete, Merge, Extract)

        private void EnsureFlattenedBeforePageStructureChange()
        {
            if (_activeSession == null)
                return;

            _activeSession.PushUndoSnapshot();
            if (_activeSession.Annotations.Count > 0)
            {
                _activeSession.WorkingPdfBytes = _pdfService.BakeAnnotationsIntoPdf(_activeSession.WorkingPdfBytes, _activeSession.Annotations);
                _activeSession.Annotations.Clear();
            }
        }

        private void RotateLeft_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            EnsureFlattenedBeforePageStructureChange();
            _activeSession.WorkingPdfBytes = _pdfService.RotatePage(_activeSession.WorkingPdfBytes, _activeSession.CurrentPageIndex, -90);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Rotated Page {_activeSession.CurrentPageNumber} 90° counter-clockwise.");
        }

        private void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            EnsureFlattenedBeforePageStructureChange();
            _activeSession.WorkingPdfBytes = _pdfService.RotatePage(_activeSession.WorkingPdfBytes, _activeSession.CurrentPageIndex, 90);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Rotated Page {_activeSession.CurrentPageNumber} 90° clockwise.");
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            EnsureFlattenedBeforePageStructureChange();
            _activeSession.WorkingPdfBytes = _pdfService.InsertBlankPage(_activeSession.WorkingPdfBytes, _activeSession.CurrentPageIndex);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            _activeSession.CurrentPageIndex++;
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Inserted blank page at Page {_activeSession.CurrentPageNumber}.");
        }

        private void DuplicatePage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            EnsureFlattenedBeforePageStructureChange();
            _activeSession.WorkingPdfBytes = _pdfService.DuplicatePage(_activeSession.WorkingPdfBytes, _activeSession.CurrentPageIndex);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            _activeSession.CurrentPageIndex++;
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Duplicated page into Page {_activeSession.CurrentPageNumber}.");
        }

        private void MovePageUp_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _activeSession.CurrentPageIndex <= 0) return;
            EnsureFlattenedBeforePageStructureChange();
            int from = _activeSession.CurrentPageIndex;
            int to = from - 1;
            _activeSession.WorkingPdfBytes = _pdfService.MovePage(_activeSession.WorkingPdfBytes, from, to);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            _activeSession.CurrentPageIndex = to;
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Moved page from {from + 1} to {to + 1}.");
        }

        private void MovePageDown_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _activeSession.CurrentPageIndex >= _activeSession.PageCount - 1) return;
            EnsureFlattenedBeforePageStructureChange();
            int from = _activeSession.CurrentPageIndex;
            int to = from + 1;
            _activeSession.WorkingPdfBytes = _pdfService.MovePage(_activeSession.WorkingPdfBytes, from, to);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            _activeSession.CurrentPageIndex = to;
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Moved page from {from + 1} to {to + 1}.");
        }

        private void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            if (_activeSession.PageCount <= 1)
            {
                MessageBox.Show("A PDF document must contain at least 1 page.", "Delete Page", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EnsureFlattenedBeforePageStructureChange();
            int deletedPageNum = _activeSession.CurrentPageNumber;
            _activeSession.WorkingPdfBytes = _pdfService.DeletePage(_activeSession.WorkingPdfBytes, _activeSession.CurrentPageIndex);
            _activeSession.IsModified = true;
            _pdfService.PopulateDocumentMetadata(_activeSession);
            _activeSession.CurrentPageIndex = Math.Clamp(_activeSession.CurrentPageIndex, 0, _activeSession.PageCount - 1);
            RebuildSessionThumbnails(_activeSession);
            RenderCurrentPageAndOverlay();
            SetStatus($"Deleted Page {deletedPageNum}. Document now has {_activeSession.PageCount} page(s).");
        }

        private void MergePdfs_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;

            var ofd = new OpenFileDialog
            {
                Title = "Select PDF File(s) to Merge into Current Document",
                Filter = "PDF Files (*.pdf)|*.pdf",
                Multiselect = true
            };

            if (ofd.ShowDialog() == true && ofd.FileNames.Length > 0)
            {
                EnsureFlattenedBeforePageStructureChange();
                var list = new List<byte[]> { _activeSession.WorkingPdfBytes };
                foreach (string f in ofd.FileNames)
                {
                    list.Add(File.ReadAllBytes(f));
                }

                _activeSession.WorkingPdfBytes = _pdfService.MergePdfDocuments(list);
                _activeSession.IsModified = true;
                _pdfService.PopulateDocumentMetadata(_activeSession);
                RebuildSessionThumbnails(_activeSession);
                RenderCurrentPageAndOverlay();
                SetStatus($"Merged {ofd.FileNames.Length} PDF file(s). Total pages: {_activeSession.PageCount}.");
            }
        }

        private void ExtractCurrentPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;

            var sfd = new SaveFileDialog
            {
                Title = $"Extract Page {_activeSession.CurrentPageNumber} to New PDF",
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_activeSession.FileName)}_Page{_activeSession.CurrentPageNumber}.pdf"
            };

            if (sfd.ShowDialog() == true)
            {
                byte[] baked = _pdfService.BakeAnnotationsIntoPdf(_activeSession.WorkingPdfBytes, _activeSession.Annotations);
                byte[] extracted = _pdfService.ExtractPageRange(baked, _activeSession.CurrentPageNumber, _activeSession.CurrentPageNumber);
                File.WriteAllBytes(sfd.FileName, extracted);
                SetStatus($"Extracted Page {_activeSession.CurrentPageNumber} to '{sfd.FileName}'.");
            }
        }

        #endregion

        #region Navigation, Search, Bookmarks, Undo/Redo & Zoom

        private void ThumbnailsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeSession == null || ThumbnailsListBox.SelectedIndex < 0)
                return;

            if (ThumbnailsListBox.SelectedIndex != _activeSession.CurrentPageIndex)
            {
                _activeSession.CurrentPageIndex = ThumbnailsListBox.SelectedIndex;
                RenderCurrentPageAndOverlay();
            }
        }

        private void BookmarksListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeSession == null || BookmarksListBox.SelectedItem is not PdfBookmarkItem bm)
                return;

            _activeSession.CurrentPageIndex = Math.Clamp(bm.PageNumber - 1, 0, _activeSession.PageCount - 1);
            RenderCurrentPageAndOverlay();
            SetStatus($"Jumped to bookmark '{bm.Title}' (Page {bm.PageNumber}).");
        }

        private void AddBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;

            var bm = new PdfBookmarkItem
            {
                Title = $"Bookmark — Page {_activeSession.CurrentPageNumber}",
                PageNumber = _activeSession.CurrentPageNumber,
                Level = 0
            };
            _activeSession.Bookmarks.Add(bm);
            SetStatus($"Added bookmark for Page {_activeSession.CurrentPageNumber}.");
        }

        private void FocusFindBox_Click(object sender, RoutedEventArgs e)
        {
            LeftTabSearch.Visibility = Visibility.Visible;
            EnsureLeftSidebarExpanded();
            LeftSidebarTabs.SelectedItem = LeftTabSearch;
            UpdateWorkspacePanelToggleButtons();

            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchSync)
                return;

            _suppressSearchSync = true;
            if (SidebarSearchBox != null && SidebarSearchBox.Text != SearchBox.Text)
            {
                SidebarSearchBox.Text = SearchBox.Text;
            }
            _suppressSearchSync = false;

            ExecuteSearch(openSidebarTab: false, advanceIfSameQuery: false);
        }

        private void SidebarSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchSync)
                return;

            _suppressSearchSync = true;
            if (SearchBox != null && SearchBox.Text != SidebarSearchBox.Text)
            {
                SearchBox.Text = SidebarSearchBox.Text;
            }
            _suppressSearchSync = false;

            ExecuteSearch(openSidebarTab: false, advanceIfSameQuery: false);
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ExecuteSearch(openSidebarTab: true, advanceIfSameQuery: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClearSearch_Click(this, new RoutedEventArgs());
            }
        }

        private void SidebarSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ExecuteSearch(openSidebarTab: true, advanceIfSameQuery: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClearSearch_Click(this, new RoutedEventArgs());
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteSearch(openSidebarTab: true, advanceIfSameQuery: true);
        }

        private void NextSearchMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            if (_activeSession.SearchMatches.Count == 0)
            {
                ExecuteSearch(openSidebarTab: true, advanceIfSameQuery: false);
                return;
            }

            int nextIdx = (_currentSearchMatchIndex + 1) % _activeSession.SearchMatches.Count;
            SelectSearchMatchByIndex(nextIdx);
        }

        private void PrevSearchMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            if (_activeSession.SearchMatches.Count == 0)
            {
                ExecuteSearch(openSidebarTab: true, advanceIfSameQuery: false);
                return;
            }

            int prevIdx = (_currentSearchMatchIndex - 1 + _activeSession.SearchMatches.Count) % _activeSession.SearchMatches.Count;
            SelectSearchMatchByIndex(prevIdx);
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            _suppressSearchSync = true;
            if (SearchBox != null) SearchBox.Text = string.Empty;
            if (SidebarSearchBox != null) SidebarSearchBox.Text = string.Empty;
            _suppressSearchSync = false;

            _lastExecutedSearchQuery = string.Empty;
            _currentSearchMatchIndex = -1;
            _highlightedSearchMatch = null;
            if (_activeSession != null)
            {
                _activeSession.SearchMatches.Clear();
            }

            if (SearchSummaryText != null) SearchSummaryText.Text = "SEARCH RESULTS";
            if (SearchMatchCounterText != null) SearchMatchCounterText.Text = string.Empty;
            RedrawOverlayCanvas();
            SetStatus("Cleared search results.");
        }

        private void ExecuteSearch(bool openSidebarTab = true, bool advanceIfSameQuery = false)
        {
            if (_activeSession == null)
                return;

            string query = (SearchBox?.Text ?? SidebarSearchBox?.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(query))
            {
                _lastExecutedSearchQuery = string.Empty;
                _currentSearchMatchIndex = -1;
                _activeSession.SearchMatches.Clear();
                _highlightedSearchMatch = null;
                if (SearchSummaryText != null) SearchSummaryText.Text = "SEARCH RESULTS";
                if (SearchMatchCounterText != null) SearchMatchCounterText.Text = string.Empty;
                RedrawOverlayCanvas();
                return;
            }

            if (advanceIfSameQuery &&
                string.Equals(query, _lastExecutedSearchQuery, StringComparison.OrdinalIgnoreCase) &&
                _activeSession.SearchMatches.Count > 0)
            {
                if (openSidebarTab)
                {
                    LeftTabSearch.Visibility = Visibility.Visible;
                    EnsureLeftSidebarExpanded();
                    LeftSidebarTabs.SelectedItem = LeftTabSearch;
                    UpdateWorkspacePanelToggleButtons();
                }

                int nextIdx = (_currentSearchMatchIndex + 1) % _activeSession.SearchMatches.Count;
                SelectSearchMatchByIndex(nextIdx);
                return;
            }

            _lastExecutedSearchQuery = query;
            _activeSession.SearchMatches.Clear();
            _highlightedSearchMatch = null;
            _currentSearchMatchIndex = -1;

            var matches = _pdfService.SearchText(_activeSession.WorkingPdfBytes, query, _activeSession.Annotations);
            foreach (var m in matches)
            {
                _activeSession.SearchMatches.Add(m);
            }

            if (SearchSummaryText != null)
            {
                SearchSummaryText.Text = $"SEARCH RESULTS ({matches.Count} matches)";
            }

            if (openSidebarTab)
            {
                LeftTabSearch.Visibility = Visibility.Visible;
                EnsureLeftSidebarExpanded();
                LeftSidebarTabs.SelectedItem = LeftTabSearch;
                UpdateWorkspacePanelToggleButtons();
            }

            if (matches.Count > 0)
            {
                SelectSearchMatchByIndex(0);
            }
            else
            {
                if (SearchMatchCounterText != null) SearchMatchCounterText.Text = "0/0";
                RedrawOverlayCanvas();
                SetStatus($"No matches found for \"{query}\".");
            }
        }

        private void SelectSearchMatchByIndex(int index)
        {
            if (_activeSession == null || index < 0 || index >= _activeSession.SearchMatches.Count)
                return;

            _currentSearchMatchIndex = index;
            var match = _activeSession.SearchMatches[index];
            _highlightedSearchMatch = match;

            int total = _activeSession.SearchMatches.Count;
            if (SearchMatchCounterText != null)
            {
                SearchMatchCounterText.Text = $"{index + 1}/{total}";
            }

            if (SearchResultsListBox != null && SearchResultsListBox.SelectedIndex != index)
            {
                SearchResultsListBox.SelectedIndex = index;
                SearchResultsListBox.ScrollIntoView(match);
            }

            _activeSession.CurrentPageIndex = Math.Clamp(match.PageNumber - 1, 0, _activeSession.PageCount - 1);
            RenderCurrentPageAndOverlay();
            ScrollMatchIntoView(match);
            SetStatus($"Match {index + 1} of {total} for \"{_lastExecutedSearchQuery}\" on Page {match.PageNumber}.");
        }

        private void ScrollMatchIntoView(PdfSearchMatch match)
        {
            if (_activeSession == null || PageScrollViewer == null)
                return;

            double zoom = _activeSession.ZoomLevel;
            double targetY = (match.Y * zoom) + 32 - (PageScrollViewer.ViewportHeight * 0.35);
            double targetX = (match.X * zoom) + 32 - (PageScrollViewer.ViewportWidth * 0.35);

            PageScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetY));
            if (PageScrollViewer.ScrollableWidth > 0)
            {
                PageScrollViewer.ScrollToHorizontalOffset(Math.Max(0, targetX));
            }
        }

        private void SearchResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeSession == null || SearchResultsListBox.SelectedItem is not PdfSearchMatch match)
                return;

            int idx = _activeSession.SearchMatches.IndexOf(match);
            if (idx >= 0 && idx != _currentSearchMatchIndex)
            {
                SelectSearchMatchByIndex(idx);
            }
        }

        private void SearchResultsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeSession == null || SearchResultsListBox.SelectedItem is not PdfSearchMatch match)
                return;

            int idx = _activeSession.SearchMatches.IndexOf(match);
            if (idx >= 0)
            {
                SelectSearchMatchByIndex(idx);
            }
        }

        private void AnnotationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeSession == null || AnnotationsListBox.SelectedItem is not PdfAnnotationItem item)
                return;

            if (_activeSession.CurrentPageIndex != item.PageIndex)
            {
                _activeSession.CurrentPageIndex = item.PageIndex;
                RenderCurrentPageAndOverlay();
            }

            _selectedAnnotation = item;
            SyncInspectorFromSelection();
            RedrawOverlayCanvas();
        }

        private void DuplicateAnnotation_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _selectedAnnotation == null)
                return;

            _activeSession.PushUndoSnapshot();
            var copy = _selectedAnnotation.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.X += 16;
            copy.Y += 16;
            _activeSession.Annotations.Add(copy);
            SelectAnnotation(copy);
            RedrawOverlayCanvas();
            RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
        }

        private void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _selectedAnnotation == null)
                return;

            _activeSession.PushUndoSnapshot();
            _activeSession.Annotations.Remove(_selectedAnnotation);
            _selectedAnnotation = null;
            SyncInspectorFromSelection();
            RedrawOverlayCanvas();
            RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
            SetStatus("Deleted selected item.");
        }

        private void ClearPageAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null)
                return;

            var onPage = _activeSession.Annotations.Where(a => a.PageIndex == _activeSession.CurrentPageIndex).ToList();
            if (onPage.Count == 0)
                return;

            _activeSession.PushUndoSnapshot();
            foreach (var item in onPage)
            {
                _activeSession.Annotations.Remove(item);
            }
            _selectedAnnotation = null;
            SyncInspectorFromSelection();
            RedrawOverlayCanvas();
            RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
            SetStatus($"Cleared {onPage.Count} annotation(s) from Page {_activeSession.CurrentPageNumber}.");
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession != null && _activeSession.Undo(out bool pdfBytesChanged))
            {
                _selectedAnnotation = null;
                SyncInspectorFromSelection();
                if (pdfBytesChanged)
                {
                    _pdfService.PopulateDocumentMetadata(_activeSession);
                    RebuildSessionThumbnails(_activeSession);
                    RenderCurrentPageAndOverlay();
                    SetStatus("Undo applied (reverted PDF watermark / page operation).");
                }
                else
                {
                    RedrawOverlayCanvas();
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                    SetStatus("Undo applied.");
                }
            }
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession != null && _activeSession.Redo(out bool pdfBytesChanged))
            {
                _selectedAnnotation = null;
                SyncInspectorFromSelection();
                if (pdfBytesChanged)
                {
                    _pdfService.PopulateDocumentMetadata(_activeSession);
                    RebuildSessionThumbnails(_activeSession);
                    RenderCurrentPageAndOverlay();
                    SetStatus("Redo applied (restored PDF watermark / page operation).");
                }
                else
                {
                    RedrawOverlayCanvas();
                    RefreshSingleThumbnail(_activeSession, _activeSession.CurrentPageIndex);
                    SetStatus("Redo applied.");
                }
            }
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UninstallApp_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Are you sure you want to uninstall ArcoCat PDF Editor from this computer?\n\nThis will close the application, remove Desktop & Start Menu shortcuts, remove Windows Explorer integration, and delete the installed program files.",
                "Uninstall ArcoCat PDF Editor",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;

            try
            {
                string localUninstallExe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uninstall.exe");
                string programsRoot = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs");
                string acroCatInstallDir = System.IO.Path.Combine(programsRoot, "AcroCatPdfEditor");
                string arcoCatInstallDir = System.IO.Path.Combine(programsRoot, "ArcoCatPdfEditor");
                string legacyInstallDir = System.IO.Path.Combine(programsRoot, "AcrobatPdfEditor");

                string? targetUninstaller = null;
                foreach (string candidateUninstall in new[]
                {
                    localUninstallExe,
                    System.IO.Path.Combine(acroCatInstallDir, "Uninstall.exe"),
                    System.IO.Path.Combine(arcoCatInstallDir, "Uninstall.exe"),
                    System.IO.Path.Combine(legacyInstallDir, "Uninstall.exe")
                })
                {
                    if (File.Exists(candidateUninstall))
                    {
                        targetUninstaller = candidateUninstall;
                        break;
                    }
                }

                if (targetUninstaller != null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetUninstaller, "--uninstall --silent")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    string[] shortcuts =
                    {
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ArcoCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AcroCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Acrobat Pro PDF Studio.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ArcoCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AcroCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall ArcoCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall AcroCat PDF Editor.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Acrobat Pro PDF Studio.lnk"),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall Acrobat Pro PDF Studio.lnk")
                    };
                    foreach (var lnk in shortcuts)
                    {
                        if (File.Exists(lnk)) File.Delete(lnk);
                    }

                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AcroCatPdfEditor", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ArcoCatPdfEditor", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AcrobatPdfEditor", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\AcroCatPdfEditor", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\ArcoCatPdfEditor", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\AcrobatPdfEditor", false);

                    string[] knownAppFiles =
                    {
                        "AcroCatPdfEditor.exe",
                        "ArcoCatPdfEditor.exe",
                        "AcrobatPdfEditor.exe",
                        "README.md",
                        "ArcoCatIcon.ico",
                        "ArcoCatIcon.png",
                        "Sample_Enterprise_Agreement.pdf",
                        "install-manifest.txt",
                        "Uninstall.exe"
                    };

                    foreach (var dir in new[] { acroCatInstallDir, arcoCatInstallDir, legacyInstallDir })
                    {
                        if (Directory.Exists(dir))
                        {
                            foreach (string f in knownAppFiles)
                            {
                                string fp = System.IO.Path.Combine(dir, f);
                                try { if (File.Exists(fp)) File.Delete(fp); } catch { }
                            }

                            // Use 'rmdir' WITHOUT '/s' so only empty dedicated app folders are removed
                            string exeInDir = System.IO.Path.Combine(dir, "AcroCatPdfEditor.exe");
                            string oldExeInDir = System.IO.Path.Combine(dir, "AcrobatPdfEditor.exe");
                            string cmdArgs = $"/C timeout /t 2 /nobreak > NUL & del /f /q \"{exeInDir}\" \"{oldExeInDir}\" 2>NUL & rmdir \"{dir}\" 2>NUL";
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmdArgs)
                            {
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WorkingDirectory = System.IO.Path.GetTempPath()
                            });
                        }
                    }
                }

                foreach (var s in _sessions)
                {
                    s.IsModified = false;
                }
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start uninstaller:\n{ex.Message}", "Uninstall Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void PresetWindowCompact_Click(object sender, RoutedEventArgs e)
        {
            FitWindowToWorkArea(1080, 700);
            SetStatus($"Window resized to {Width:F0} × {Height:F0}.");
        }

        private void PresetWindowStandard_Click(object sender, RoutedEventArgs e)
        {
            FitWindowToWorkArea(1280, 820);
            SetStatus($"Window resized and centered ({Width:F0} × {Height:F0}).");
        }

        private void CenterWindowOnScreen_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(Width, workArea.Width * 0.95);
            Height = Math.Min(Height, workArea.Height * 0.93);
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2.0);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2.0);
            SetStatus("Window centered on screen.");
        }

        private void TopWindowDragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? curr = dep;
                while (curr != null && curr != sender)
                {
                    if (curr is ButtonBase || curr is TextBoxBase || curr is MenuItem || curr is ComboBox)
                        return;
                    curr = VisualTreeHelper.GetParent(curr);
                }
            }

            if (e.ClickCount == 2)
            {
                MaximizeRestoreWindow_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    if (WindowState == WindowState.Maximized)
                    {
                        var mousePt = e.GetPosition(this);
                        var screenPt = PointToScreen(mousePt);
                        double ratioX = mousePt.X / Math.Max(1, ActualWidth);
                        WindowState = WindowState.Normal;
                        Left = screenPt.X - (Width * ratioX);
                        Top = Math.Max(0, screenPt.Y - 14);
                    }
                    DragMove();
                }
                catch
                {
                    // Ignore DragMove exceptions if mouse button was released
                }
            }
        }

        private void WindowResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                var workArea = SystemParameters.WorkArea;
                WindowState = WindowState.Normal;
                Left = workArea.Left + 20;
                Top = workArea.Top + 20;
                Width = workArea.Width * 0.90;
                Height = workArea.Height * 0.88;
            }

            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (BtnMaxRestoreWin != null)
            {
                BtnMaxRestoreWin.Content = WindowState == WindowState.Maximized
                    ? "🗗 Restore"
                    : "🗖 Maximize";
            }
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var modifiedSessions = _sessions.Where(s => s.IsModified).ToList();
            if (modifiedSessions.Count > 0)
            {
                var res = MessageBox.Show(
                    $"You have unsaved changes in {modifiedSessions.Count} document(s). Save changes before exiting?",
                    "Exit ArcoCat PDF Editor",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (res == MessageBoxResult.Yes)
                {
                    foreach (var s in modifiedSessions)
                    {
                        SaveSessionToFile(s, forceDialog: false);
                    }
                }
            }
        }

        private void FirstPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            _activeSession.CurrentPageIndex = 0;
            RenderCurrentPageAndOverlay();
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _activeSession.CurrentPageIndex <= 0) return;
            _activeSession.CurrentPageIndex--;
            RenderCurrentPageAndOverlay();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _activeSession.CurrentPageIndex >= _activeSession.PageCount - 1) return;
            _activeSession.CurrentPageIndex++;
            RenderCurrentPageAndOverlay();
        }

        private void LastPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            _activeSession.CurrentPageIndex = _activeSession.PageCount - 1;
            RenderCurrentPageAndOverlay();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
            => SetZoom((_activeSession?.ZoomLevel ?? 1.0) + 0.15);

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
            => SetZoom((_activeSession?.ZoomLevel ?? 1.0) - 0.15);

        private void FitWidth_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            Size pageSz = _activeSession.PageSizesPt.TryGetValue(_activeSession.CurrentPageIndex, out var s) ? s : new Size(612, 792);
            double availW = Math.Max(300, PageScrollViewer.ViewportWidth - 80);
            SetZoom(availW / pageSz.Width);
        }

        private void FitPage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null) return;
            Size pageSz = _activeSession.PageSizesPt.TryGetValue(_activeSession.CurrentPageIndex, out var s) ? s : new Size(612, 792);
            double availW = Math.Max(300, PageScrollViewer.ViewportWidth - 80);
            double availH = Math.Max(300, PageScrollViewer.ViewportHeight - 80);
            SetZoom(Math.Min(availW / pageSz.Width, availH / pageSz.Height));
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_activeSession == null) return;
            if (Math.Abs(_activeSession.ZoomLevel - ZoomSlider.Value) > 0.01)
            {
                SetZoom(ZoomSlider.Value);
            }
        }

        private void SetZoom(double newZoom)
        {
            if (_activeSession == null) return;
            double clamped = Math.Clamp(newZoom, 0.5, 3.0);
            _activeSession.ZoomLevel = clamped;
            if (Math.Abs(ZoomSlider.Value - clamped) > 0.01)
            {
                ZoomSlider.Value = clamped;
            }
            RenderCurrentPageAndOverlay();
        }

        private void PageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                double delta = e.Delta > 0 ? 0.12 : -0.12;
                SetZoom((_activeSession?.ZoomLevel ?? 1.0) + delta);
            }
        }

        private void ShowDetectedTextBlocksCheck_Changed(object sender, RoutedEventArgs e)
        {
            RedrawOverlayCanvas();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T)
            {
                ReopenClosedTab_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.O) { OpenPdf_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.S) { SavePdf_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.W) { CloseActiveTab_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.T) { ReopenClosedTab_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.Z) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.Y) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.F) { FocusFindBox_Click(this, new RoutedEventArgs()); e.Handled = true; }
            }
            else if (e.Key == Key.F3)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    PrevSearchMatch_Click(this, new RoutedEventArgs());
                else
                    NextSearchMatch_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                MaximizeRestoreWindow_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation != null && !PropTextBox.IsKeyboardFocusWithin)
            {
                DeleteAnnotation_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        #endregion

        #region Helpers

        private void UpdateDocumentInfoPanel()
        {
            if (_activeSession == null)
                return;

            DocInfoTitle.Text = $"Title: {_activeSession.DocTitle}";
            DocInfoAuthor.Text = $"Author: {_activeSession.DocAuthor}";
            Size sz = _activeSession.PageSizesPt.TryGetValue(_activeSession.CurrentPageIndex, out var s) ? s : new Size(612, 792);
            DocInfoPageSize.Text = $"Page Size: {sz.Width / 72.0:F1} × {sz.Height / 72.0:F1} in ({sz.Width:F0} × {sz.Height:F0} pt)";
            DocInfoVersion.Text = $"PDF Version: {_activeSession.PdfVersion}   |   Pages: {_activeSession.PageCount}";
        }

        private void SetStatus(string message)
        {
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = message;
            }
        }

        private static Color ParseMediaColor(string hex, double opacity)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex) || hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                    return Colors.Transparent;

                var c = (Color)ColorConverter.ConvertFromString(hex);
                byte a = (byte)Math.Clamp(opacity * 255, 0, 255);
                return Color.FromArgb(a, c.R, c.G, c.B);
            }
            catch
            {
                return Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), 229, 57, 53);
            }
        }

        #endregion
    }
}

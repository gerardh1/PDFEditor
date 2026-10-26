using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AcrobatPdfEditor.Views
{
    public partial class SignatureDialog : Window
    {
        private bool _isDrawing;
        private Polyline? _currentStroke;
        private string _selectedColorHex = "#0D47A1";

        public byte[]? SignatureImageBytes { get; private set; }
        public string SignatureText { get; private set; } = string.Empty;
        public string SelectedInkHex => _selectedColorHex;

        public SignatureDialog()
        {
            InitializeComponent();
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (SignatureCanvas == null || TypedSignaturePanel == null)
                return;

            bool isDraw = DrawModeRadio.IsChecked == true;
            SignatureCanvas.Visibility = isDraw ? Visibility.Visible : Visibility.Collapsed;
            TypedSignaturePanel.Visibility = isDraw ? Visibility.Collapsed : Visibility.Visible;
        }

        private void InkColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InkColorCombo.SelectedItem is ComboBoxItem item && item.Tag is string hex)
            {
                _selectedColorHex = hex;
                if (TypedPreviewText != null)
                {
                    TypedPreviewText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                }
            }
        }

        private void TypedNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TypedPreviewText != null)
            {
                TypedPreviewText.Text = string.IsNullOrWhiteSpace(TypedNameBox.Text) ? "Signature" : TypedNameBox.Text;
            }
        }

        private void SignatureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDrawing = true;
            SignatureCanvas.CaptureMouse();
            var pt = e.GetPosition(SignatureCanvas);

            _currentStroke = new Polyline
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColorHex)),
                StrokeThickness = 2.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            _currentStroke.Points.Add(pt);
            SignatureCanvas.Children.Add(_currentStroke);
        }

        private void SignatureCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDrawing || _currentStroke == null)
                return;

            var pt = e.GetPosition(SignatureCanvas);
            _currentStroke.Points.Add(pt);
        }

        private void SignatureCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                SignatureCanvas.ReleaseMouseCapture();
                _currentStroke = null;
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            SignatureCanvas.Children.Clear();
            if (TypedNameBox != null)
            {
                TypedNameBox.Text = string.Empty;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (DrawModeRadio.IsChecked == true)
            {
                if (SignatureCanvas.Children.Count == 0)
                {
                    MessageBox.Show("Please draw your signature on the pad or switch to Type Signature.", "Signature Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SignatureImageBytes = RenderElementToPngBytes(SignatureCanvas, (int)Math.Max(300, SignatureCanvas.ActualWidth), (int)Math.Max(140, SignatureCanvas.ActualHeight));
                SignatureText = "Drawn Signature";
            }
            else
            {
                string typed = TypedNameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(typed))
                {
                    MessageBox.Show("Please enter your name for the signature.", "Signature Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SignatureText = typed;
                SignatureImageBytes = RenderTypedSignatureToPngBytes(typed, _selectedColorHex);
            }

            DialogResult = true;
            Close();
        }

        private static byte[] RenderElementToPngBytes(FrameworkElement element, int width, int height)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var vb = new VisualBrush(element) { Stretch = Stretch.Uniform };
                dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] RenderTypedSignatureToPngBytes(string text, string colorHex)
        {
            var border = new Border
            {
                Width = 340,
                Height = 90,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Times New Roman"),
                    FontSize = 36,
                    FontStyle = FontStyles.Italic,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            border.Measure(new Size(340, 90));
            border.Arrange(new Rect(0, 0, 340, 90));
            return RenderElementToPngBytes(border, 340, 90);
        }
    }
}

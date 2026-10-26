using System;
using System.Windows;
using System.Windows.Controls;

namespace AcrobatPdfEditor.Views
{
    public partial class WatermarkDialog : Window
    {
        public string WatermarkText { get; private set; } = "CONFIDENTIAL";
        public string WatermarkColorHex { get; private set; } = "#D32F2F";
        public double WatermarkFontSize { get; private set; } = 56;
        public double WatermarkAngle { get; private set; } = -35;
        public double WatermarkOpacity { get; private set; } = 0.22;
        public bool ApplyToAllPages { get; private set; } = true;

        public WatermarkDialog()
        {
            InitializeComponent();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            string txt = WatermarkTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(txt))
            {
                MessageBox.Show("Please enter watermark text.", "Watermark", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            WatermarkText = txt;
            if (ColorCombo.SelectedItem is ComboBoxItem cItem && cItem.Tag is string hex)
            {
                WatermarkColorHex = hex;
            }

            if (double.TryParse(FontSizeBox.Text, out double sz))
            {
                WatermarkFontSize = Math.Clamp(sz, 14, 140);
            }

            if (AngleCombo.SelectedItem is ComboBoxItem aItem && aItem.Tag is string angStr && double.TryParse(angStr, out double ang))
            {
                WatermarkAngle = ang;
            }

            WatermarkOpacity = OpacitySlider.Value;
            ApplyToAllPages = AllPagesCheck.IsChecked == true;

            DialogResult = true;
            Close();
        }
    }
}

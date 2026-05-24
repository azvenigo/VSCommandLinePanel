using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace VS_LaunchArguments
{
    // Data row for the history list. Time is Unix epoch seconds; TimeText is the
    // formatted localized display string (lazily computed, no allocation per layout).
    public class HistoryEntry
    {
        public string Command { get; set; }
        public long   Time    { get; set; }

        public string TimeText =>
            Time > 0
                ? DateTimeOffset.FromUnixTimeSeconds(Time).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
    }

    // Non-modal picker window. Owned by the VS main window so it stays above it but
    // closes automatically when the user clicks back to VS (Deactivated). Escape and
    // the Cancel button dismiss without selecting; OK or double-click commits.
    public partial class HistoryWindow : Window
    {
        public event EventHandler<string> CommandSelected;

        public HistoryWindow(IList<HistoryEntry> entries, UIElement anchorElement)
        {
            InitializeComponent();

            HistoryList.ItemsSource = entries;
            if (entries.Count > 0)
                HistoryList.SelectedIndex = 0;

            Loaded += (s, e) =>
            {
                PositionNearAnchor(anchorElement);
                HistoryList.Focus();
            };
        }

        private void PositionNearAnchor(UIElement anchor)
        {
            try
            {
                // PointToScreen returns physical pixels; WPF Left/Top are logical (device-independent) pixels.
                // We need the DPI scale to convert between them.
                var source     = PresentationSource.FromVisual(anchor);
                double dpiX    = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY    = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                var physicalPos  = anchor.PointToScreen(new Point(0, 0));
                double logicalX  = physicalPos.X / dpiX;
                double logicalY  = physicalPos.Y / dpiY;
                double anchorH   = anchor.RenderSize.Height;

                // Get work area in physical pixels, convert to logical
                var screen     = System.Windows.Forms.Screen.FromPoint(
                                     new System.Drawing.Point((int)physicalPos.X, (int)physicalPos.Y));
                double waLeft   = screen.WorkingArea.Left   / dpiX;
                double waTop    = screen.WorkingArea.Top    / dpiY;
                double waRight  = screen.WorkingArea.Right  / dpiX;
                double waBottom = screen.WorkingArea.Bottom / dpiY;

                // Prefer above the strip; fall back to below if not enough room
                double top = logicalY - ActualHeight;
                if (top < waTop)
                    top = logicalY + anchorH;

                Left = Math.Max(waLeft, Math.Min(logicalX, waRight  - ActualWidth));
                Top  = Math.Max(waTop,  Math.Min(top,      waBottom - ActualHeight));
            }
            catch { }
        }

        private void OnOkClick(object sender, RoutedEventArgs e)             => CommitSelection();
        private void OnCancelClick(object sender, RoutedEventArgs e)         => Close();
        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) => CommitSelection();

        private void CommitSelection()
        {
            var entry = HistoryList.SelectedItem as HistoryEntry;
            if (entry != null)
                CommandSelected?.Invoke(this, entry.Command);
            Close();
        }
    }
}

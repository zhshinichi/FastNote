using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace FastNote
{
    /// <summary>Read-only view of one record, opened by double-clicking a card.</summary>
    public class DetailWindow : Window
    {
        public DetailWindow(string text, string richContent, double textScale = 1.0)
        {
            Title = "查看全文";
            Width = 560;
            Height = 380;
            MinWidth = 360;
            MinHeight = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F5F7FA"));
            FontFamily = UiFonts.Ui;

            var box = new RichTextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.White,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#293B55")),
                // Follow the main window's 字号 choice so the full text reads the same.
                FontSize = Math.Round(14 * Math.Clamp(textScale, 1.0, 1.5), 1),
                Padding = new Thickness(12),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(16)
            };
            RichDocument.ConfigureEditor(box);
            RichDocument.Load(box.Document, text, richContent);
            Content = box;
            Loaded += (s, e) => { box.Focus(); box.CaretPosition = box.Document.ContentStart; };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    Close();
            };
        }
    }
}

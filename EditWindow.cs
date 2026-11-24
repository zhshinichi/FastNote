using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using DockPanel = System.Windows.Controls.DockPanel;
using RichTextBox = System.Windows.Controls.RichTextBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FastNote
{
    /// <summary>Edits one record: text plus any inline images, same store format.</summary>
    public class EditWindow : Window
    {
        private readonly RichTextBox box;

        public string EditedText { get; private set; }
        public string EditedRich { get; private set; }

        public EditWindow(string text, string richContent, double textScale = 1.0)
        {
            Title = "编辑事项";
            Width = 620;
            Height = 440;
            MinWidth = 420;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F7FA"));
            FontFamily = UiFonts.Ui;

            box = new RichTextBox
            {
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.White,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#293B55")),
                // Follow the main window's 字号 choice so editing matches the list.
                FontSize = Math.Round(14 * Math.Clamp(textScale, 1.0, 1.5), 1),
                Padding = new Thickness(12),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(16, 16, 16, 10)
            };
            RichDocument.ConfigureEditor(box);
            RichDocument.Load(box.Document, text, richContent);

            var save = new Button
            {
                Content = "保存",
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 16, 12),
                IsDefault = false
            };
            save.Click += (s, e) => Accept();

            var cancel = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 12),
                IsCancel = true
            };
            cancel.Click += (s, e) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(new TextBlock
            {
                Text = "Ctrl+Enter 保存",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B98AA")),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 12)
            });
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(box);
            Content = root;

            Loaded += (s, e) =>
            {
                box.Focus();
                box.CaretPosition = box.Document.ContentEnd;
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Accept();
                    e.Handled = true;
                }
            };
        }

        private void Accept()
        {
            EditedText = RichDocument.Text(box.Document).Trim();
            EditedRich = RichDocument.Serialize(box.Document);
            if (EditedText.Length == 0 && EditedRich == null)
                return; // nothing to store; keep the dialog open
            DialogResult = true;
            Close();
        }
    }
}

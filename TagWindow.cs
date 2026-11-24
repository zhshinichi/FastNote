using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using DockPanel = System.Windows.Controls.DockPanel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace FastNote
{
    /// <summary>Asks for a tag name. One short field, Enter to confirm, Esc to back out.</summary>
    public class TagWindow : Window
    {
        private readonly TextBox box;

        public string TagName { get; private set; }

        public TagWindow(string title, string initial, double textScale = 1.0)
        {
            Title = title;
            Width = 380;
            Height = 176;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F7FA"));
            FontFamily = UiFonts.Ui;
            SizeToContent = SizeToContent.Manual;

            var label = new TextBlock
            {
                Text = "标签名",
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#78889D")),
                Margin = new Thickness(16, 16, 16, 6)
            };

            box = new TextBox
            {
                Text = initial ?? "",
                FontSize = Math.Round(13.5 * Math.Clamp(textScale, 1.0, 1.5), 1),
                Height = 34,
                Padding = new Thickness(9, 0, 9, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCE3EC")),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(16, 0, 16, 0)
            };

            var ok = new Button { Content = "确定", Width = 82, Height = 30, Margin = new Thickness(0, 0, 16, 12), IsDefault = true };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Content = "取消", Width = 82, Height = 30, Margin = new Thickness(0, 0, 8, 12), IsCancel = true };
            cancel.Click += (s, e) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var field = new StackPanel();
            field.Children.Add(label);
            field.Children.Add(box);
            root.Children.Add(field);
            Content = root;

            Loaded += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    Accept();
                    e.Handled = true;
                }
            };
        }

        private void Accept()
        {
            var name = Clean(box.Text);
            if (name.Length == 0)
            {
                box.Focus();
                return;
            }
            TagName = name;
            DialogResult = true;
            Close();
        }

        /// <summary>Tags live in a TSV column and in a chip: keep them short and single-line.</summary>
        public static string Clean(string value)
        {
            var text = (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  "))
                text = text.Replace("  ", " ");
            if (text.StartsWith("#"))
                text = text.TrimStart('#').Trim();
            return text.Length > 24 ? text.Substring(0, 24) : text;
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace FastNote
{
    /// <summary>Tray menu → 关于: version, where the data is, and the font notice the font licence asks for.</summary>
    public class AboutWindow : Window
    {
        public AboutWindow(string dataDirectory, string hotkeyText)
        {
            Title = "关于 FastNote";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#F5F7FA");
            FontFamily = UiFonts.Ui;

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var root = new WpfStackPanel { Margin = new Thickness(24, 20, 24, 16) };
            root.Children.Add(new WpfTextBlock
            {
                Text = "FastNote",
                FontSize = 20,
                FontFamily = UiFonts.Medium,
                FontWeight = FontWeights.Medium,
                Foreground = Brush("#171C26")
            });
            root.Children.Add(Line("版本 " + (version == null ? "?" : version.ToString(3)), "#868F9F", 2));
            root.Children.Add(Line("呼出窗口：" + hotkeyText, "#4A5363", 14));
            root.Children.Add(Line("数据：" + dataDirectory, "#4A5363", 4));
            root.Children.Add(Line("界面字体为 HarmonyOS Sans SC，© 2021 Huawei Device Co., Ltd.，" +
                                   "依照 HarmonyOS Sans Fonts License Agreement 随程序分发。", "#4A5363", 14));
            root.Children.Add(Line("FastNote 以 MIT 许可开源。", "#4A5363", 4));

            var buttons = new WpfStackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var license = Path.Combine(AppContext.BaseDirectory, "Fonts", "HarmonyOS_Sans_LICENSE.txt");
            buttons.Children.Add(Button("字体许可", () => Open(license), File.Exists(license)));
            buttons.Children.Add(Button("打开数据文件夹", () => Open(dataDirectory), Directory.Exists(dataDirectory)));
            var close = Button("关闭", Close, true);
            close.IsCancel = true;
            close.IsDefault = true;
            close.Margin = new Thickness(0);
            buttons.Children.Add(close);
            root.Children.Add(buttons);
            Content = root;
        }

        private static WpfTextBlock Line(string text, string color, double top)
        {
            return new WpfTextBlock
            {
                Text = text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(color),
                Margin = new Thickness(0, top, 0, 0)
            };
        }

        private static WpfButton Button(string text, Action click, bool enabled)
        {
            var button = new WpfButton
            {
                Content = text,
                MinWidth = 82,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = enabled
            };
            button.Click += (s, e) => click();
            return button;
        }

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // nothing useful to say if Explorer refuses
            }
        }

        private static WpfBrush Brush(string hex)
        {
            return new WpfBrush((WpfColor)WpfColorConverter.ConvertFromString(hex));
        }
    }
}

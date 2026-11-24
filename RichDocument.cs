using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using Image = System.Windows.Controls.Image;
using RichTextBox = System.Windows.Controls.RichTextBox;
using Binding = System.Windows.Data.Binding;
using BindingOperations = System.Windows.Data.BindingOperations;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace FastNote
{
    internal static class RichDocument
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> normalizedImages = new();
        private static readonly Style paragraphStyle = CreateParagraphStyle();

        private static Style CreateParagraphStyle()
        {
            var style = new Style(typeof(Paragraph));
            style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            style.Seal();
            return style;
        }

        public static BitmapSource NormalizeImage(BitmapSource source)
        {
            return normalizedImages.GetValue(source, bitmap =>
            {
                // Some Windows clipboard producers supply RGB pixels with every
                // alpha byte left at zero. Preserve real transparency, but repair
                // this invalid all-transparent screenshot format.
                var readable = bitmap.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                               bitmap.Format == System.Windows.Media.PixelFormats.Pbgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int stride = checked(readable.PixelWidth * 4);
                var pixels = new byte[checked(stride * readable.PixelHeight)];
                readable.CopyPixels(pixels, stride, 0);
                bool hasColor = false;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] != 0) return bitmap;
                    hasColor |= pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0;
                }
                if (!hasColor) return bitmap;
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                var repaired = BitmapSource.Create(readable.PixelWidth, readable.PixelHeight,
                    readable.DpiX, readable.DpiY, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
                repaired.Freeze();
                return repaired;
            });
        }

        public static TextRange Range(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd);
        public static string Text(FlowDocument document)
        {
            // WPF appends one paragraph terminator to every document.
            var text = Range(document).Text;
            return text.EndsWith("\r\n", StringComparison.Ordinal) ? text.Substring(0, text.Length - 2) : text;
        }

        public static List<Image> Images(FlowDocument document)
        {
            var images = new List<Image>();
            var pointer = document.ContentStart;
            while (pointer != null && pointer.CompareTo(document.ContentEnd) < 0)
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement &&
                    pointer.GetAdjacentElement(LogicalDirection.Forward) is Image image)
                    images.Add(image);
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
            return images;
        }

        public static string Serialize(FlowDocument document)
        {
            if (Images(document).Count == 0) return null;
            using var stream = new MemoryStream();
            Range(document).Save(stream, DataFormats.XamlPackage);
            return Convert.ToBase64String(stream.ToArray());
        }

        public static void Load(FlowDocument document, string text, string richContent)
        {
            if (!string.IsNullOrEmpty(richContent))
            {
                try
                {
                    using var stream = new MemoryStream(Convert.FromBase64String(richContent));
                    Range(document).Load(stream, DataFormats.XamlPackage);
                    foreach (var image in Images(document))
                        if (image.Source is BitmapSource bitmap) image.Source = NormalizeImage(bitmap);
                    return;
                }
                catch (Exception) { /* Keep the plain text fallback accessible. */ }
            }
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(text ?? "")));
        }

        public static BitmapSource ReadImage(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return NormalizeImage(image);
        }

        public static void ConfigureEditor(RichTextBox editor)
        {
            editor.Document.Resources[typeof(Paragraph)] = paragraphStyle;
            bool fitting = false;
            void Fit()
            {
                if (fitting) return;
                fitting = true;
                try { FitImages(editor); }
                finally { fitting = false; }
            }
            editor.SizeChanged += (s, e) => Fit();
            editor.TextChanged += (s, e) => Fit();
            editor.Loaded += (s, e) => Fit();
        }

        public static FlowDocument CreateEditorDocument(RichTextBox editor, string text, string richContent)
        {
            var document = new FlowDocument();
            document.Resources[typeof(Paragraph)] = paragraphStyle;
            BindingOperations.SetBinding(document, TextElement.FontSizeProperty,
                new Binding(nameof(editor.FontSize)) { Source = editor });
            BindingOperations.SetBinding(document, TextElement.FontFamilyProperty,
                new Binding(nameof(editor.FontFamily)) { Source = editor });
            BindingOperations.SetBinding(document, TextElement.ForegroundProperty,
                new Binding(nameof(editor.Foreground)) { Source = editor });
            Load(document, text, richContent);
            return document;
        }

        public static void FitImages(RichTextBox editor)
        {
            // Inline images share a line with the caret. Keep that entire line
            // inside the viewport so moving the caret cannot hide the image.
            var maxWidth = Math.Max(48, Math.Min(600, editor.ActualWidth - 40));
            var maxHeight = Math.Max(24, editor.ActualHeight - 28);
            foreach (var image in Images(editor.Document))
            {
                if (image.Source is BitmapSource bitmap)
                {
                    var normalized = NormalizeImage(bitmap);
                    if (!ReferenceEquals(normalized, bitmap)) image.Source = normalized;
                }
                if (image.MaxWidth != maxWidth) image.MaxWidth = maxWidth;
                if (image.MaxHeight != maxHeight) image.MaxHeight = maxHeight;
            }
        }

        public static void InsertImage(RichTextBox editor, BitmapSource source)
        {
            editor.Selection.Text = "";
            var image = new Image { Source = NormalizeImage(source), MaxWidth = 600, MaxHeight = 500, Stretch = System.Windows.Media.Stretch.Uniform };
            var container = new InlineUIContainer(image, editor.CaretPosition);
            FitImages(editor);
            editor.CaretPosition = container.ElementEnd;
            editor.Focus();
        }

        public static DataObject CreateClipboardData(FlowDocument document)
        {
            var range = Range(document);
            var images = Images(document);
            var text = Text(document);
            var data = new DataObject();
            if (!string.IsNullOrEmpty(text)) data.SetData(DataFormats.UnicodeText, text);
            if (images.Count > 0)
            {
                var stream = new MemoryStream();
                range.Save(stream, DataFormats.XamlPackage);
                stream.Position = 0;
                data.SetData(DataFormats.XamlPackage, stream);
                // RTF lets other rich-text applications receive mixed text and images.
                using var rtf = new MemoryStream();
                range.Save(rtf, DataFormats.Rtf);
                data.SetData(DataFormats.Rtf, System.Text.Encoding.UTF8.GetString(rtf.ToArray()));
                if (images.Count == 1 && string.IsNullOrWhiteSpace(text) && images[0].Source is BitmapSource bitmap)
                    data.SetData(DataFormats.Bitmap, bitmap);
            }
            return data;
        }

        public static void Copy(FlowDocument document)
        {
            var data = CreateClipboardData(document);
            if (data.GetFormats().Length > 0) Clipboard.SetDataObject(data, true);
        }
    }
}

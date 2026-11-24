using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Clipboard = System.Windows.Clipboard;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace FastNote
{
    public partial class MainWindow : Window
    {
        private const int WmHotkey = 0x0312;
        private const int WmGetMinMaxInfo = 0x0024;
        private const int HotkeyId = 0xA11;
        // The tick path is 16.14px long; dash values are in pen-thickness units.
        private const double TickDash = 8.07;
        // Grouped mode shows this many rows per week before collapsing to a footer.
        private const int WeekPreviewCount = 5;
        // The card text is 14pt at 100%; the A button and Ctrl+wheel move along
        // this ladder so every step is a visible change.
        private const double BaseTextSize = 14;
        private static readonly (double Scale, string Label)[] TextScalePresets =
        {
            (1.00, "标准"),
            (1.15, "大"),
            (1.30, "特大"),
            (1.50, "超大")
        };
        private static readonly double[] TextScaleLadder = { 1.00, 1.07, 1.15, 1.22, 1.30, 1.40, 1.50 };

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public int Flags;
        }

        private readonly Store store;
        private readonly DispatcherTimer draftTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer searchTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        // Dragging a window edge fires SizeChanged dozens of times; one write at the
        // end is enough, and the old behaviour hit the disk on every frame.
        private readonly DispatcherTimer layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private WeekSummary openWeek;
        private WeekSummary doneWeek;
        private readonly Dictionary<ListBox, ListMode> listModes = new();
        private WinForms.NotifyIcon tray;
        private int hotkeyAttempts;
        private bool applyingLayout;
        private double textScale = 1.0;
        private Idea editingIdea;
        private bool composerEditChanged;
        private ParkedDraft parkedDraft;

        private sealed record ParkedDraft(FlowDocument Document, TextPointer SelectionStart,
            TextPointer SelectionEnd, double ScrollOffset);

        /// <summary>How a column renders its records: plain cards, week groups, or one drilled week.</summary>
        private enum ListMode
        {
            Cards,
            Grouped
        }

        public bool ReallyExit { get; private set; }

        public MainWindow(string dataDirectory = null)
        {
            InitializeComponent();
            store = new Store(dataDirectory);
            store.Load();
            tagNames.AddRange(store.LoadTags());

            OpenList.ItemsSource = openView;
            DoneList.ItemsSource = doneView;
            textScale = store.LoadTextScale();
            ApplyTextScale();

            RichDocument.ConfigureEditor(Composer);
            RichDocument.Load(Composer.Document, store.LoadDraft(), store.LoadRichDraft());
            Composer.TextChanged += (s, e) =>
            {
                UpdateComposerHint();
                if (editingIdea != null)
                {
                    composerEditChanged = true;
                    return;
                }
                draftTimer.Stop();
                draftTimer.Start();
            };
            Composer.GotKeyboardFocus += (s, e) => UpdateComposerHint();
            Composer.LostKeyboardFocus += (s, e) => UpdateComposerHint();
            UpdateComposerHint();
            draftTimer.Tick += (s, e) => SaveDraft();
            searchTimer.Tick += (s, e) => Refresh();
            feedbackTimer.Tick += (s, e) => { feedbackTimer.Stop(); ResetCopyCaptions(); };
            layoutSaveTimer.Tick += (s, e) => { layoutSaveTimer.Stop(); SaveLayoutNow(); };

            Refresh();
            OpenList.PreviewMouseWheel += List_PreviewMouseWheel;
            DoneList.PreviewMouseWheel += List_PreviewMouseWheel;
            PreviewMouseLeftButtonDown += OnCardMouseDown;
            PreviewMouseMove += OnDragMouseMove;
            PreviewMouseLeftButtonUp += OnDragMouseUp;
            LostMouseCapture += OnLostMouseCapture;
            Deactivated += (s2, e2) => AbortPendingGesture();
            longPressTimer.Tick += LongPressTimer_Tick;
            dragWatchdog.Tick += DragWatchdog_Tick;
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            Closing += MainWindow_Closing;
            SizeChanged += (s, e) => PersistRatio();
            StateChanged += MainWindow_StateChanged;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            // Building the tray icon (WinForms NotifyIcon + icon extraction) costs
            // ~130ms. The window should be up before that, so it waits for idle.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (tray == null)
                        CreateTray();
                }));
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            applyingLayout = true;
            var composerHeight = store.LoadComposerHeight();
            // The restyled composer has no title row; heights saved by the old
            // layout (160-200) leave it half empty, so they snap to the new default.
            if (composerHeight >= 150 && composerHeight <= 200)
                composerHeight = 128;
            ComposerRow.Height = new GridLength(composerHeight);
            var ratio = store.LoadSplitRatio();
            OpenColumn.Width = new GridLength(ratio, GridUnitType.Star);
            DoneColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
            applyingLayout = false;

            bool pinned = store.LoadPinned();
            PinButton.IsChecked = pinned;
            Topmost = pinned;
            Composer.Focus();
            Composer.CaretPosition = Composer.Document.ContentEnd;

        }

        // ------------------------------------------------------------ text size

        /// <summary>
        /// Enlarges what you read: the shared size resources below are what every
        /// card, week row, the composer and both dialogs bind to, so one write
        /// here rescales them live. Buttons and window chrome keep their own
        /// fixed sizes, so only the text grows and the layout stays put.
        /// </summary>
        private void ApplyTextScale()
        {
            textScale = Math.Clamp(textScale, 1.0, 1.5);

            SetSizeResource("FsCard", 14.5);
            SetSizeResource("FsCardLine", 23);
            SetSizeResource("FsCardMax", 46);
            SetSizeResource("FsMeta", 12);
            SetSizeResource("FsCompact", 13.5);
            SetSizeResource("FsCompactTime", 11.5);
            SetSizeResource("FsComposer", 15);
            SetSizeResource("FsGroup", 13);
            SetSizeResource("FsGroupSmall", 11.5);
            SetSizeResource("FsGroupFooter", 12);

            if (FontValue != null)
            {
                FontValue.Text = (BaseTextSize * textScale).ToString("0");
                FontButton.ToolTip = "字号 " + FontValue.Text + " · Ctrl+滚轮";
            }
        }

        private void SetSizeResource(string key, double baseSize)
        {
            Resources[key] = Math.Round(baseSize * textScale, 1);
        }

        private void FontButton_Click(object sender, RoutedEventArgs e)
        {
            var ladder = NearestTextScale();
            var menu = new ContextMenu { FontSize = 14 };

            var bigger = new MenuItem { Header = "放大", InputGestureText = "Ctrl + =" };
            bigger.IsEnabled = ladder < TextScaleLadder.Length - 1;
            bigger.Click += (s, args) => StepTextScale(1);
            menu.Items.Add(bigger);

            var smaller = new MenuItem { Header = "缩小", InputGestureText = "Ctrl + -" };
            smaller.IsEnabled = ladder > 0;
            smaller.Click += (s, args) => StepTextScale(-1);
            menu.Items.Add(smaller);

            menu.Items.Add(new Separator());

            foreach (var preset in TextScalePresets)
            {
                var size = (BaseTextSize * preset.Scale).ToString("0");
                var current = Math.Abs(preset.Scale - textScale) < 0.005;
                var item = new MenuItem
                {
                    Header = (current ? "●  " : "    ") + preset.Label + "  ·  " + size + " 号"
                };
                System.Windows.Automation.AutomationProperties.SetName(item, "字号 " + preset.Label + " " + size);
                var scale = preset.Scale;
                item.Click += (s, args) => SetTextScale(scale);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = FontButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>Closest rung of the ladder, so a saved 1.22 still steps cleanly.</summary>
        private int NearestTextScale()
        {
            var index = 0;
            var best = double.MaxValue;
            for (var i = 0; i < TextScaleLadder.Length; i++)
            {
                var distance = Math.Abs(TextScaleLadder[i] - textScale);
                if (distance < best)
                {
                    best = distance;
                    index = i;
                }
            }
            return index;
        }

        private void StepTextScale(int direction)
        {
            var index = Math.Clamp(NearestTextScale() + direction, 0, TextScaleLadder.Length - 1);
            SetTextScale(TextScaleLadder[index]);
        }

        private void SetTextScale(double scale)
        {
            scale = Math.Clamp(scale, TextScaleLadder[0], TextScaleLadder[TextScaleLadder.Length - 1]);
            if (Math.Abs(scale - textScale) < 0.001)
                return;
            textScale = scale;
            ApplyTextScale();
            // layout.ini keeps this next to the divider ratio, so it survives Alt+Q.
            PersistRatio();
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0)
                return;
            StepTextScale(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        // ------------------------------------------------------------ wheel

        /// <summary>
        /// One notch moves a fixed slice of a card instead of a whole item, and
        /// follows the 字号 so it feels the same at 14 and at 21.
        /// </summary>
        private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                return;   // Ctrl+wheel is the text size

            var list = sender as ListBox;
            var viewer = list == null ? null : FindScrollViewer(list);
            if (viewer == null)
                return;

            var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            var step = notches * 56 * textScale * (e.Delta > 0 ? -1 : 1);
            viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset + step, 0, viewer.ScrollableHeight));
            e.Handled = true;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer)
                    return viewer;
                var found = FindScrollViewer(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        // -------------------------------------------------- long press → drag → 搁置

        /// <summary>How long the button must stay down before a card is picked up.</summary>
        private const int LongPressMilliseconds = 260;
        private const double LongPressTolerance = 10;
        private const double ArmedLift = -42;

        private readonly DispatcherTimer longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds)
        };

        /// <summary>
        /// Ends a drag whose mouse-up never arrived (capture stolen, focus lost):
        /// otherwise the layer stays up, we keep the mouse captured and every click
        /// in the window is swallowed - the app looks frozen.
        /// </summary>
        private readonly DispatcherTimer dragWatchdog = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        private Point pressPoint;
        private Point grabOffset;
        private const double PlaceholderOpacity = 0.4;

        /// <summary>
        /// Set by "--uitest" (which requires a sandbox --data-dir and --instance).
        /// Drags are then driven from code: no mouse capture and no real-button
        /// checks, so a test never touches the user's mouse.
        /// </summary>
        public static bool UiTestMode { get; set; }
        // The ghost's transforms, applied in this order: scale and tilt around the
        // card's centre first, then the pose offset, then pointer tracking. (The old
        // order translated first and scaled after, so every shrink also pulled the
        // card toward the window's top-left corner: that is why a card hovering a
        // tag chip sat in the wrong place.)
        private ScaleTransform ghostScale;
        private RotateTransform ghostSpin;
        private TranslateTransform ghostLift;
        private TranslateTransform ghostFollow;
        private enum GhostPose { Carry, Bin, Chip }
        private GhostPose ghostPose;
        private Point lastDragPointer;
        // Over a chip the token rides on the chip's row instead of the pointer's
        // exact height; this is how far that anchor sits from the pointer.
        private double ghostAnchorDy;
        private Rect hoverChipBounds;
        private Idea pressIdea;
        private ListBoxItem pressItem;
        private Idea dragIdea;
        private ListBoxItem dragItem;
        private FrameworkElement dragGhost;
        private Rect binBounds;
        private bool pressArmed;
        private bool draggingCard;
        private bool binArmed;

        // ---------------------------------------------------------------- tags
        private readonly IdeaList openView = new();
        private readonly IdeaList doneView = new();
        private readonly Dictionary<DateTime, WeekSummary> openWeeks = new();
        private readonly Dictionary<DateTime, WeekSummary> doneWeeks = new();
        private List<TagBucket> tagBuckets = new();
        private string paintedTag;
        private string paintedLabel;

        private string currentTag;
        /// <summary>Second level inside the tag: the one-line gist, null = all of them.</summary>
        private string currentLabel;
        private readonly List<Button> labelChips = new List<Button>();
        private readonly List<string> tagNames = new List<string>();
        private readonly List<Button> tagChips = new List<Button>();
        /// <summary>Chip tag -> its number, so a count repaints without touching the row.</summary>
        private readonly Dictionary<string, TextBlock> tagCountText = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        private TextBlock allTagCountText;
        private Button hoverTagChip;
        private string hoverTag;

        private static readonly SolidColorBrush BinPlateIdle = FrozenBrush("#FFFCF6");
        private static readonly SolidColorBrush BinPlateHot = FrozenBrush("#FFF3DC");
        private static readonly SolidColorBrush BinEdgeIdle = FrozenBrush("#E3C28C");
        private static readonly SolidColorBrush BinEdgeHot = FrozenBrush("#D89320");
        private static readonly SolidColorBrush BinInkIdle = FrozenBrush("#C08A2E");
        private static readonly SolidColorBrush BinInkHot = FrozenBrush("#A9741B");

        private static SolidColorBrush FrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (draggingCard && dragGhost == null)
            {
                // A drag that lost its ghost would otherwise swallow every click.
                draggingCard = false;
                dragWatchdog.Stop();
                if (IsMouseCaptured)
                    ReleaseMouseCapture();
            }
            if (draggingCard || e.ChangedButton != MouseButton.Left)
                return;
            // A press while the tray is still fading out belongs to the last gesture.
            if (dragGhost != null || DragLayer.Opacity > 0.02)
                return;
            if (e.OriginalSource is not DependencyObject source)
                return;
            // The circle and the star are buttons: pressing them must not pick the card up.
            if (FindParent<System.Windows.Controls.Primitives.ButtonBase>(source) != null)
                return;

            var item = FindParent<ListBoxItem>(source);
            if (item == null || FindParent<ListBox>(item) != OpenList)
                return;
            if (item.DataContext is not Idea idea || idea.Done)
                return;

            pressItem = item;
            pressIdea = idea;
            pressPoint = e.GetPosition(this);
            pressArmed = false;
            longPressTimer.Start();
        }

        private void OnDragMouseMove(object sender, MouseEventArgs e)
        {
            var point = e.GetPosition(this);
            if (draggingCard)
            {
                MoveDragGhost(point);
                return;
            }

            if (pressIdea == null || Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            var delta = point - pressPoint;
            var moved = Math.Abs(delta.X) > LongPressTolerance || Math.Abs(delta.Y) > LongPressTolerance;

            if (pressArmed)
            {
                if (Math.Abs(delta.X) > 2 || Math.Abs(delta.Y) > 2)
                    BeginCardDrag(point);
                return;
            }

            // Dragging straight away is the gesture people actually make; the hold
            // is only a second way in for a careful pick-up.
            if (moved)
                BeginCardDrag(point);
        }

        private void OnDragMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;
            if (!draggingCard)
            {
                CancelLongPress();
                return;
            }
            EndCardDrag(binBounds.Contains(e.GetPosition(this)));
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            // Taking capture for the drag makes the card under the cursor lose it,
            // and that loss bubbles up here. Only a real drop-out (we no longer own
            // the mouse) should cancel.
            if (draggingCard && !IsMouseCaptured)
                CancelDrag();
        }

        private void DragWatchdog_Tick(object sender, EventArgs e)
        {
            if (!draggingCard || dragGhost == null)
            {
                dragWatchdog.Stop();
                return;
            }
            if (UiTestMode || Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            EndCardDrag(false);
        }

        private void LongPressTimer_Tick(object sender, EventArgs e)
        {
            longPressTimer.Stop();
            if (draggingCard || pressIdea == null || pressItem == null)
                return;
            if (!IsAncestorOf(pressItem))
            {
                CancelLongPress();
                return;
            }

            // The hold itself answers "did it hear me?": the card lifts and the tray
            // slides up. Nothing else moves until the pointer does.
            pressArmed = true;
            LiftCard();
            ShowTray();
        }

        private void LiftCard()
        {
            if (pressItem == null)
                return;
            pressItem.RenderTransformOrigin = new Point(0.5, 0.5);
            var lift = new ScaleTransform(1, 1);
            pressItem.RenderTransform = lift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            lift.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 1.03, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            lift.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 1.03, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        }

        /// <summary>Brings the tray up and refreshes its drop rect. Safe to call twice.</summary>
        private void ShowTray()
        {
            binBounds = ComputeBinBounds();
            ShelveBin.BeginAnimation(OpacityProperty, null);
            ShelveBin.Opacity = 1;
            BinLabel.Text = pressIdea != null && pressIdea.Shelved ? "拖到这里取回" : "拖到这里搁置";
            SetBinArmed(false, true);
            if (DragLayer.Opacity > 0.99)
                return;
            DragLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(DragLayer.Opacity, 1, TimeSpan.FromMilliseconds(130)));
            PopBinIn();
        }

        private Rect ComputeBinBounds()
        {
            // The layer is always laid out (it just sits at opacity 0), so these sizes
            // are valid without forcing another layout pass when a drag starts.
            var width = ShelveBin.ActualWidth > 1 ? ShelveBin.ActualWidth : 280;
            var height = ShelveBin.ActualHeight > 1 ? ShelveBin.ActualHeight : 140;
            var rect = new Rect(
                (DragLayer.ActualWidth - width) / 2,
                DragLayer.ActualHeight - ShelveBin.Margin.Bottom - height,
                width, height);
            rect.Inflate(18, 18);
            return rect;
        }

        private void CancelLongPress()
        {
            longPressTimer.Stop();
            if (draggingCard)
                return;
            var trayWasUp = pressArmed;
            if (pressItem != null)
                pressItem.RenderTransform = null;
            pressArmed = false;
            pressIdea = null;
            pressItem = null;
            if (trayWasUp)
                FadeLayerOut();
        }

        /// <summary>
        /// Drops a half-started gesture. A dialog that takes the mouse away mid-press
        /// (double-click → editor) must not leave the tray hanging on screen.
        /// </summary>
        private void AbortPendingGesture()
        {
            longPressTimer.Stop();
            pressArmed = false;
            pressIdea = null;
            if (pressItem != null)
            {
                pressItem.RenderTransform = null;
                pressItem = null;
            }
            if (draggingCard)
                CancelDrag();
            else
                FadeLayerOut();
        }

        private void BeginCardDrag(Point pointer)
        {
            if (pressIdea == null || pressItem == null)
                return;
            if (!IsAncestorOf(pressItem))
            {
                CancelLongPress();
                return;
            }

            longPressTimer.Stop();
            // The lift preview would end up in the snapshot; drop it first.
            pressItem.RenderTransform = null;

            // The layer shares the window's coordinate space, but it is a sibling of
            // the lists (not their ancestor), so measure against the window itself.
            var bounds = pressItem.TransformToAncestor(this)
                .TransformBounds(new Rect(pressItem.RenderSize));
            // A card half scrolled out of the list: carry only the part you can see.
            var host = FindParent<ListBox>(pressItem);
            if (host != null)
            {
                var viewport = host.TransformToAncestor(this).TransformBounds(new Rect(host.RenderSize));
                var visible = Rect.Intersect(bounds, viewport);
                if (!visible.IsEmpty && visible.Width > 20 && visible.Height > 12)
                    bounds = visible;
            }
            grabOffset = new Point(
                Math.Clamp(pointer.X - bounds.Left, 0, Math.Max(0, bounds.Width)),
                Math.Clamp(pointer.Y - bounds.Top, 0, Math.Max(0, bounds.Height)));

            dragGhost = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                CornerRadius = new CornerRadius(12),
                Background = CardSnapshot(bounds),
                RenderTransformOrigin = new Point(0.5, 0.5),
                // Outline as a child, so the snapshot keeps its full size and stays sharp.
                Child = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    BorderBrush = (Brush)FindResource("Accent"),
                    BorderThickness = new Thickness(1.5)
                }

                // No DropShadowEffect and no Canvas positioning: a blurred effect on
                // an element that moves with every mouse event is what made the drag
                // stutter, and moving it through a translate keeps layout out of the
                // loop entirely.
            };

            ghostScale = new ScaleTransform(1, 1);
            ghostSpin = new RotateTransform(0);
            ghostLift = new TranslateTransform();
            ghostFollow = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(ghostScale);
            group.Children.Add(ghostSpin);
            group.Children.Add(ghostLift);
            group.Children.Add(ghostFollow);
            dragGhost.RenderTransform = group;

            Canvas.SetLeft(dragGhost, 0);
            Canvas.SetTop(dragGhost, 0);
            ghostFollow.X = pointer.X - grabOffset.X;
            ghostFollow.Y = pointer.Y - grabOffset.Y;
            lastDragPointer = pointer;
            DragCanvas.Children.Add(dragGhost);
            ghostAnchorDy = 0;
            ghostPose = GhostPose.Bin;          // force the first pose to animate
            ApplyGhostPose(GhostPose.Carry, 0);

            draggingCard = true;
            // The slot the card came from: a soft placeholder, faded in over a beat
            // so it reads as "the card lifted out" rather than "it went grey".
            pressItem.BeginAnimation(OpacityProperty, null);
            pressItem.Opacity = 1;
            pressItem.BeginAnimation(OpacityProperty, new DoubleAnimation(1, PlaceholderOpacity, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
            ShowTray();
            if (!UiTestMode)
                CaptureMouse();
            dragWatchdog.Start();
        }

        /// <summary>The last picture taken for a drag; the self-test checks it is not blank.</summary>
        private System.Windows.Media.Imaging.BitmapSource lastSnapshot;

        /// <summary>
        /// A frozen picture of the card, so dimming the original cannot fade the ghost.
        /// Rendering the card element directly shifts it by its offset inside the list
        /// (that cut cards off), and going through a VisualBrush came out blank for
        /// cards after the first. Rendering the window itself has neither problem, so
        /// the card is cut out of a picture of the whole window.
        /// </summary>
        private ImageBrush CardSnapshot(Rect bounds)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            var full = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);

            // Keep the drag layer (tray, a fading ghost or ripple) out of the picture.
            var layerVisibility = DragLayer.Visibility;
            DragLayer.Visibility = Visibility.Hidden;
            try
            {
                full.Render(this);
            }
            finally
            {
                DragLayer.Visibility = layerVisibility;
            }

            var x = Math.Clamp((int)Math.Round(bounds.Left * dpi.DpiScaleX), 0, width - 1);
            var y = Math.Clamp((int)Math.Round(bounds.Top * dpi.DpiScaleY), 0, height - 1);
            var w = Math.Clamp((int)Math.Round(bounds.Width * dpi.DpiScaleX), 1, width - x);
            var h = Math.Clamp((int)Math.Round(bounds.Height * dpi.DpiScaleY), 1, height - y);
            var card = new System.Windows.Media.Imaging.CroppedBitmap(full, new Int32Rect(x, y, w, h));
            card.Freeze();
            lastSnapshot = card;
            return new ImageBrush(card) { Stretch = Stretch.Fill };
        }

        /// <summary>Share of opaque pixels and number of dark (text) pixels in a picture.</summary>
        private static (double coverage, int ink) SnapshotStats(System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            if (bitmap == null)
                return (0, 0);
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            int opaque = 0, ink = 0, total = converted.PixelWidth * converted.PixelHeight;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a > 200)
                {
                    opaque++;
                    var luminance = (pixels[i] + pixels[i + 1] * 2 + pixels[i + 2]) / 4;
                    if (luminance < 110)
                        ink++;
                }
            }
            return (total == 0 ? 0 : (double)opaque / total, ink);
        }

        private void MoveDragGhost(Point pointer)
        {
            if (dragGhost == null || ghostFollow == null)
                return;
            lastDragPointer = pointer;
            UpdateTagHover(pointer);
            var overChip = hoverTagChip != null;
            var overBin = !overChip && binBounds.Contains(pointer);
            SetBinArmed(overBin, false);
            var pose = overChip ? GhostPose.Chip : overBin ? GhostPose.Bin : GhostPose.Carry;

            // Over a chip the token locks to the chip's row, so it always hangs below
            // the chip whatever height inside the chip the pointer is at. The switch
            // in and out is handed to the lift animation (liftJump), so it glides.
            var anchorDy = overChip ? hoverChipBounds.Top + hoverChipBounds.Height / 2 - pointer.Y : 0;
            var liftJump = anchorDy - ghostAnchorDy;
            ghostAnchorDy = anchorDy;
            ghostFollow.X = pointer.X - grabOffset.X;
            ghostFollow.Y = pointer.Y + anchorDy - grabOffset.Y;
            if (pose != ghostPose)
                ApplyGhostPose(pose, liftJump);
        }

        /// <summary>
        /// While dragging, the tag chips are drop targets. The test is a little
        /// forgiving (a few px around each chip) and only counts chips you can see.
        /// </summary>
        private void UpdateTagHover(Point pointer)
        {
            Button hit = null;
            if (TagStripScroll.IsVisible)
            {
                var strip = TagStripScroll.TransformToAncestor(this)
                    .TransformBounds(new Rect(TagStripScroll.RenderSize));
                strip.Inflate(0, 10);
                if (strip.Contains(pointer))
                {
                    foreach (var chip in tagChips)
                    {
                        if (chip.Tag is not string || !chip.IsVisible)
                            continue;
                        var bounds = chip.TransformToAncestor(this).TransformBounds(new Rect(chip.RenderSize));
                        var zone = bounds;
                        zone.Inflate(3, 10);
                        if (zone.Contains(pointer))
                        {
                            hit = chip;
                            if (!ReferenceEquals(chip, hoverTagChip))
                                hoverChipBounds = bounds;
                            break;
                        }
                    }
                }
            }

            if (ReferenceEquals(hit, hoverTagChip))
                return;

            var previous = hoverTagChip;
            hoverTagChip = hit;
            hoverTag = hit?.Tag as string;
            ApplyChipColors(previous);
            ApplyChipColors(hit);
            if (previous != null)
                AnimateChipScale(previous, 1, 180, new CubicEase { EasingMode = EasingMode.EaseOut });
            if (hit != null)
                AnimateChipScale(hit, 1.08, 220, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        }

        private static ScaleTransform ChipScale(Button chip)
        {
            if (chip.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
                return existing;
            chip.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(1, 1);
            chip.RenderTransform = scale;
            return scale;
        }

        private static void AnimateChipScale(Button chip, double to, int milliseconds, IEasingFunction ease)
        {
            if (chip == null)
                return;
            var scale = ChipScale(chip);
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = ease });
        }

        /// <summary>
        /// Three ways of carrying a card. Only runs when the pose changes, so mouse
        /// moves never restart the animations (that restart is what felt stiff).
        ///  - Carry: full size, the grabbed spot stays under the pointer.
        ///  - Bin:   shrinks around the pointer and floats up over the tray.
        ///  - Chip:  becomes a small token hanging just below-right of the pointer,
        ///           so the chip under the pointer stays readable.
        /// </summary>
        private void ApplyGhostPose(GhostPose pose, double liftJumpY)
        {
            if (dragGhost == null || ghostScale == null)
                return;
            ghostPose = pose;

            var w = dragGhost.Width;
            var h = dragGhost.Height;
            var centre = new Point(w / 2, h / 2);
            double s, angle, lx, ly;
            switch (pose)
            {
                case GhostPose.Chip:
                    s = Math.Clamp(170 / Math.Max(1, w), 0.24, 0.5);
                    angle = 0;
                    // The anchor is (pointer.X, chip centre). Put the token's top-left
                    // corner 10px left of the pointer and 8px below the chip's bottom
                    // edge, so the chip label is never covered.
                    lx = grabOffset.X - centre.X + (-10 + s * w / 2);
                    ly = grabOffset.Y - centre.Y + (hoverChipBounds.Height / 2 + 8 + s * h / 2);
                    break;
                case GhostPose.Bin:
                    s = 0.46;
                    angle = -4.5;
                    lx = (grabOffset.X - centre.X) * (1 - s);
                    ly = (grabOffset.Y - centre.Y) * (1 - s) + ArmedLift;
                    break;
                default:
                    // 1:1 and untilted: a rotated or scaled bitmap of text looks blurred
                    // and doubled over its own placeholder. The outline says "picked up".
                    s = 1;
                    angle = 0;
                    lx = (grabOffset.X - centre.X) * (1 - s);
                    ly = (grabOffset.Y - centre.Y) * (1 - s);
                    break;
            }

            var duration = TimeSpan.FromMilliseconds(pose == GhostPose.Chip ? 260 : 220);
            IEasingFunction sizeEase = pose == GhostPose.Carry
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var moveEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
            ghostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s, duration) { EasingFunction = sizeEase });
            ghostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s, duration) { EasingFunction = sizeEase });
            ghostSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, duration) { EasingFunction = moveEase });
            ghostLift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(lx, duration) { EasingFunction = moveEase });
            ghostLift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(ghostLift.Y - liftJumpY, ly, duration) { EasingFunction = moveEase });
            dragGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(pose == GhostPose.Chip ? 0.96 : 1, duration));
        }

        /// <summary>
        /// Dropped on a chip: the card arcs into the chip (x eases out, y eases in,
        /// so the path curves instead of sliding in a straight line), the chip
        /// catches it with a bounce and a ring, and the card in the list fades back
        /// in already wearing its new tag.
        /// </summary>
        private void DropIntoChip(string tag)
        {
            var idea = dragIdea;
            var item = dragItem;
            var chip = hoverTagChip;
            ShelveBin.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));

            var target = lastDragPointer;
            if (chip != null && chip.IsVisible)
            {
                var bounds = chip.TransformToAncestor(this).TransformBounds(new Rect(chip.RenderSize));
                target = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }

            if (dragGhost != null && ghostFollow != null)
            {
                var w = dragGhost.Width;
                var h = dragGhost.Height;
                var duration = TimeSpan.FromMilliseconds(280);
                var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                var inEase = new CubicEase { EasingMode = EasingMode.EaseIn };
                // centre = follow + (w/2, h/2) + lift  ->  lift to 0, follow to target - centre.
                ghostLift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = outEase });
                ghostLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = inEase });
                ghostFollow.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(target.X - w / 2, duration) { EasingFunction = outEase });
                ghostFollow.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(target.Y - h / 2, duration) { EasingFunction = inEase });
                var shrink = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 };
                ghostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.05, duration) { EasingFunction = shrink });
                ghostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.05, duration) { EasingFunction = shrink });
                ghostSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-8, duration) { EasingFunction = inEase });
                dragGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110))
                {
                    BeginTime = TimeSpan.FromMilliseconds(190)
                });
            }

            After(250, () =>
            {
                // Arrival: drop the hover look, write the tag, celebrate on the chip.
                var hovered = hoverTagChip;
                hoverTagChip = null;
                hoverTag = null;
                ApplyChipColors(hovered);

                AssignTag(idea, tag);

                var landed = tagChips.FirstOrDefault(c => string.Equals(c.Tag as string, tag, StringComparison.Ordinal));
                PulseChip(landed);
                ChipRipple(landed);

                RestoreCard(item);
                var container = idea == null ? null : OpenList.ItemContainerGenerator.ContainerFromItem(idea) as ListBoxItem;
                if (container != null)
                {
                    container.Opacity = 1;
                    container.BeginAnimation(OpacityProperty, new DoubleAnimation(PlaceholderOpacity, 1, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop
                    });
                }
            });
            After(470, FinishCardDrag);
        }

        /// <summary>A ring that grows out of the chip and fades: "it went in here".</summary>
        private void ChipRipple(Button chip)
        {
            if (chip == null || !chip.IsVisible)
                return;
            var bounds = chip.TransformToAncestor(this).TransformBounds(new Rect(chip.RenderSize));
            var ring = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                CornerRadius = new CornerRadius(bounds.Height / 2),
                BorderBrush = (Brush)FindResource("Accent"),
                BorderThickness = new Thickness(2),
                Opacity = 0.55,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var grow = new ScaleTransform(1, 1);
            ring.RenderTransform = grow;
            Canvas.SetLeft(ring, bounds.Left);
            Canvas.SetTop(ring, bounds.Top);
            DragCanvas.Children.Add(ring);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(380);
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1 + 26 / Math.Max(20, bounds.Width), duration) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.7, duration) { EasingFunction = ease });
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, duration) { EasingFunction = ease });
            After(400, () => DragCanvas.Children.Remove(ring));
        }

        private void SetBinArmed(bool armed, bool force)
        {
            if (!force && binArmed == armed)
                return;
            binArmed = armed;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var scale = armed ? 1.09 : 1.0;
            BinScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            BinScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });

            BinPlate.Fill = armed ? BinPlateHot : BinPlateIdle;
            BinPlate.Stroke = armed ? BinEdgeHot : BinEdgeIdle;
            BinGlyph.Stroke = armed ? BinInkHot : BinInkIdle;
            BinLabel.Foreground = armed ? BinInkHot : BinInkIdle;

            var shelved = pressIdea != null && pressIdea.Shelved;
            BinLabel.Text = armed
                ? (shelved ? "松开取回" : "松开搁置")
                : (shelved ? "拖到这里取回" : "拖到这里搁置");

        }

        private void PopBinIn()
        {
            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            BinScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = back });
            BinScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = back });
            BinShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        private void EndCardDrag(bool intoBin)
        {
            if (dragGhost == null || pressIdea == null)
            {
                CancelDrag();
                return;
            }

            // Hand the gesture over to the card it belongs to and drop the press
            // state right away: otherwise the next mouse move (button still down
            // after a quick second press) would start a phantom second drag.
            dragIdea = pressIdea;
            dragItem = pressItem;
            pressIdea = null;
            pressItem = null;
            pressArmed = false;

            draggingCard = false;
            dragWatchdog.Stop();
            ReleaseMouseCapture();

            if (hoverTag != null)
            {
                DropIntoChip(hoverTag);
                return;
            }

            if (intoBin)
            {
                FlyIntoBin();
                return;
            }

            SpringBack();
        }

        /// <summary>Released over nothing: the card glides back into its slot.</summary>
        private void SpringBack()
        {
            ShelveBin.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            var item = dragItem;
            Rect? home = null;
            if (item != null && item.IsVisible && IsAncestorOf(item))
                home = item.TransformToAncestor(this).TransformBounds(new Rect(item.RenderSize));

            if (home == null || ghostFollow == null)
            {
                RestoreCard(item);
                dragGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
                After(150, FinishCardDrag);
                return;
            }

            var duration = TimeSpan.FromMilliseconds(240);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ghostLift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            ghostLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            ghostFollow.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(home.Value.Left, duration) { EasingFunction = ease });
            ghostFollow.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(home.Value.Top, duration) { EasingFunction = ease });
            ghostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            ghostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            ghostSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            dragGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
            After(240, () =>
            {
                RestoreCard(item);
                dragGhost?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)));
            });
            After(330, FinishCardDrag);
        }

        private static void RestoreCard(UIElement item)
        {
            if (item == null)
                return;
            item.BeginAnimation(OpacityProperty, null);
            item.Opacity = 1;
        }

        // ------------------------------------------------------------ drag self-test

        /// <summary>
        /// "--uitest --uitest-drag=&lt;folder&gt;": plays the three drag endings in code
        /// (into a tag chip, back to its slot, into the 搁置 tray), saves frames
        /// mid-animation and writes measured positions plus PASS/FAIL lines to
        /// drag-selftest.txt. Only runs in UiTestMode, i.e. on a sandbox profile.
        /// </summary>
        public async void RunDragSelfTest(string folder)
        {
            var log = new System.Text.StringBuilder();
            var failures = 0;
            void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
            string R(Rect r) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "x={0:0.#} y={1:0.#} w={2:0.#} h={3:0.#}", r.X, r.Y, r.Width, r.Height);
            async Task Wait(int ms) => await System.Threading.Tasks.Task.Delay(ms);
            void Frame(string name)
            {
                UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
                encoder.Save(file);
            }
            Rect Bounds(FrameworkElement element) =>
                element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
            Rect GhostBounds() => dragGhost == null ? Rect.Empty
                : dragGhost.TransformToAncestor(this).TransformBounds(new Rect(dragGhost.RenderSize));
            ListBoxItem Container(int index)
            {
                OpenList.UpdateLayout();
                return OpenList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            }
            async Task Glide(Point from, Point to, int steps)
            {
                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / steps;
                    MoveDragGhost(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
                    await Wait(16);
                }
            }
            bool Pick(ListBoxItem item, Point pointer)
            {
                if (item?.DataContext is not Idea idea)
                    return false;
                pressItem = item;
                pressIdea = idea;
                pressPoint = pointer;
                pressArmed = true;
                lastSnapshot = null;
                BeginCardDrag(pointer);
                var (coverage, ink) = SnapshotStats(lastSnapshot);
                Check(coverage > 0.9 && ink > 40, "ghost picture has the card in it (opaque " +
                    (coverage * 100).ToString("0") + "%, text pixels " + ink + ")");
                return dragGhost != null;
            }

            try
            {
                if (!UiTestMode)
                    return;
                System.IO.Directory.CreateDirectory(folder);
                await Wait(400);

                // ---- 1. into a tag chip
                var chip = tagChips.FirstOrDefault(c => c.Tag is string && c.IsVisible);
                var first = Container(0);
                Check(chip != null, "a tag chip is on screen");
                Check(first != null, "first open card has a container");
                if (chip != null && first != null)
                {
                    var tag = (string)chip.Tag;
                    var idea = (Idea)first.DataContext;
                    var card = Bounds(first);
                    var start = new Point(card.Left + 60, card.Top + 20);
                    Check(Pick(first, start), "drag starts");
                    await Wait(60);
                    var ghost = GhostBounds();
                    log.AppendLine("card  " + R(card));
                    log.AppendLine("ghost " + R(ghost) + "  (just picked up)");
                    Check(Math.Abs(ghost.Left - card.Left) < 6 && Math.Abs(ghost.Top - card.Top) < 6,
                        "ghost starts on top of the card");
                    Frame("drag-1-picked.png");

                    var chipBox = Bounds(chip);
                    var target = new Point(chipBox.Left + chipBox.Width / 2, chipBox.Top + chipBox.Height / 2);
                    await Glide(start, target, 18);
                    await Wait(320);
                    ghost = GhostBounds();
                    log.AppendLine("chip  " + R(chipBox) + "  tag=" + tag);
                    log.AppendLine("ptr   " + target);
                    log.AppendLine("ghost " + R(ghost) + "  (over chip)");
                    Check(ReferenceEquals(hoverTagChip, chip), "chip is the drop target");
                    Check(Math.Abs(ghost.Left - (target.X - 10)) < 6, "token starts 10px left of the pointer");
                    Check(ghost.Top >= chipBox.Bottom + 2 && ghost.Top <= chipBox.Bottom + 16,
                        "token hangs just below the chip (token top " + ghost.Top.ToString("0.#") +
                        ", chip bottom " + chipBox.Bottom.ToString("0.#") + ")");
                    Check(ghost.Width < 200, "token is small (" + ghost.Width.ToString("0") + "px wide)");
                    Frame("drag-2-over-chip.png");
                    MoveDragGhost(new Point(target.X, chipBox.Top + 4));
                    await Wait(80);
                    var nudged = GhostBounds();
                    Check(Math.Abs(nudged.Top - ghost.Top) < 1.5,
                        "token stays below the chip when the pointer is near the chip's top (" + nudged.Top.ToString("0.#") + ")");
                    Check(ReferenceEquals(hoverTagChip, chip), "chip is still the target near its top edge");

                    EndCardDrag(false);
                    await Wait(130);
                    Frame("drag-3-flying.png");
                    await Wait(170);
                    Frame("drag-4-landed.png");
                    await Wait(700);
                    Check(string.Equals(idea.Tag, tag, StringComparison.Ordinal), "record now has tag " + tag);
                    Check(dragGhost == null, "ghost removed");
                    Check(DragCanvas.Children.Count == 0, "drag layer is empty (" + DragCanvas.Children.Count + ")");
                    var landed = OpenList.ItemContainerGenerator.ContainerFromItem(idea) as ListBoxItem;
                    Check(landed == null || landed.Opacity > 0.99, "card is fully visible again");
                }

                // ---- 2. released over nothing: glides home
                await Wait(300);
                var second = Container(1) ?? Container(0);
                if (second != null)
                {
                    var idea = (Idea)second.DataContext;
                    var before = idea.Tag;
                    var card = Bounds(second);
                    var start = new Point(card.Left + 60, card.Top + 20);
                    Check(Pick(second, start), "second drag starts");
                    await Wait(200);
                    Check(Math.Abs(second.Opacity - PlaceholderOpacity) < 0.05, "slot fades to placeholder (" + second.Opacity.ToString("0.00") + ")");
                    var away = new Point(start.X + 140, start.Y + 60);
                    await Glide(start, away, 12);
                    await Wait(200);
                    EndCardDrag(false);
                    await Wait(120);
                    Frame("drag-5-springing-back.png");
                    var ghost = GhostBounds();
                    log.AppendLine("ghost " + R(ghost) + "  (on its way home, card " + R(card) + ")");
                    await Wait(600);
                    Check(string.Equals(idea.Tag, before, StringComparison.Ordinal), "tag unchanged after empty drop");
                    Check(second.Opacity > 0.99, "card opacity back to 1 (" + second.Opacity.ToString("0.00") + ")");
                    Check(dragGhost == null && DragCanvas.Children.Count == 0, "drag layer cleared");
                }

                // ---- 3. into the 搁置 tray
                await Wait(300);
                var third = Container(2) ?? Container(0);
                if (third != null)
                {
                    var idea = (Idea)third.DataContext;
                    var card = Bounds(third);
                    var start = new Point(card.Left + 60, card.Top + 20);
                    Check(Pick(third, start), "third drag starts");
                    await Wait(150);
                    var binCentre = new Point(binBounds.Left + binBounds.Width / 2, binBounds.Top + binBounds.Height / 2);
                    await Glide(start, binCentre, 18);
                    await Wait(300);
                    Frame("drag-6-over-tray.png");
                    var ghost = GhostBounds();
                    log.AppendLine("tray  " + R(binBounds));
                    log.AppendLine("ghost " + R(ghost) + "  (over tray)");
                    Check(binBounds.Contains(new Point(ghost.Left + ghost.Width / 2, ghost.Top + ghost.Height / 2)),
                        "shrunken card floats over the tray");
                    EndCardDrag(true);
                    await Wait(150);
                    Frame("drag-7-into-tray.png");
                    await Wait(800);
                    Check(idea.Shelved, "record is 搁置 now");
                    Check(dragGhost == null && DragCanvas.Children.Count == 0, "drag layer cleared");
                    var anyGrey = false;
                    for (var i = 0; i < OpenList.Items.Count; i++)
                        if (OpenList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c && c.Opacity < 0.99)
                            anyGrey = true;
                    Check(!anyGrey, "no card left greyed out");
                }

                // ---- 4. Enter finishes a card: tick, the slot closes, it flies to 已完成
                await Wait(400);
                var toFinish = Container(0);
                if (toFinish?.DataContext is Idea finishing)
                {
                    CompleteIdea(finishing);
                    await Wait(150);
                    Frame("drag-8-complete-tick.png");
                    await Wait(260);
                    Frame("drag-9-complete-lift.png");
                    Check(FlyCanvas.Children.Count == 1, "a picture of the finished card is on its way (" + FlyCanvas.Children.Count + ")");
                    await Wait(200);
                    Frame("drag-10-complete-flying.png");
                    await Wait(900);
                    Frame("drag-11-complete-landed.png");
                    Check(finishing.Done, "the record is finished");
                    Check(FlyCanvas.Children.Count == 0, "the fly layer is cleared");
                    var landedRow = DoneList.ItemContainerGenerator.ContainerFromItem(finishing) as ListBoxItem;
                    Check(landedRow == null || (landedRow.Opacity > 0.99 && landedRow.RenderTransform == null),
                        "its 已完成 row is fully shown");
                    var squashed = false;
                    for (var i = 0; i < OpenList.Items.Count; i++)
                        if (OpenList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem openRow &&
                            (openRow.Opacity < 0.99 || !double.IsNaN(openRow.Height)))
                            squashed = true;
                    Check(!squashed, "no card in 未完成 is left hidden or squashed");
                    CompleteIdea(finishing);
                    await Wait(120);
                    Check(FlyCanvas.Children.Count == 0, "finishing a finished card does nothing");

                    RestoreIdea(finishing);
                    await Wait(100);
                    Frame("drag-12-restore.png");
                    await Wait(400);
                    var back = OpenList.ItemContainerGenerator.ContainerFromItem(finishing) as ListBoxItem;
                    Check(!finishing.Done && (back == null || (back.Opacity > 0.99 && back.RenderTransform == null)),
                        "Enter on a finished row brings it back, fully shown");
                }
                else Check(false, "an open card to finish");

                // ---- 5. finishing an old record leaves 已完成 where it was
                await Wait(300);
                var doneViewer = FindScrollViewer(DoneList);
                var doneOffset = doneViewer?.VerticalOffset ?? 0;
                var oldest = OpenList.Items.Count > 1 ? OpenList.Items[OpenList.Items.Count - 1] as Idea : null;
                if (oldest != null)
                {
                    OpenList.SelectedItem = oldest;
                    CompleteIdea(oldest);
                    await Wait(1300);
                    Check(oldest.Done || oldest.IsQuestion, "the oldest record is finished");
                    var after = doneViewer?.VerticalOffset ?? 0;
                    Check(Math.Abs(after - doneOffset) < 1.5, "已完成 is not scrolled to chase it (offset " +
                        doneOffset.ToString("0.#") + " -> " + after.ToString("0.#") + ")");
                    Check(OpenList.SelectedItem is Idea nextUp && !nextUp.Done, "the next open record is selected for another Enter");
                    Check(FlyCanvas.Children.Count == 0, "the fly layer is cleared after an off-screen landing");
                }

                // ---- 6. questions are finished by answering them
                await Wait(300);
                if (Container(0)?.DataContext is Idea asked)
                {
                    asked.IsQuestion = true;
                    OpenList.SelectedItem = asked;
                    await Wait(200);
                    Frame("drag-13-question-card.png");
                    CompleteIdea(asked);
                    await Wait(350);
                    Check(AnswerOpen && !asked.Done, "a question opens the answer sheet instead of finishing");
                    Frame("drag-14-answer-sheet.png");
                    SubmitAnswer(true);
                    await Wait(400);
                    Check(AnswerOpen && !asked.Done, "an empty answer cannot finish it");
                    Frame("drag-15-answer-empty-nudge.png");
                    AnswerBox.Text = "先用二分把范围缩到一半，再逐个验证。\n边界情况单独写测试。";
                    await Wait(150);
                    Frame("drag-16-answer-typed.png");
                    SubmitAnswer(true);
                    await Wait(1500);
                    Check(!AnswerOpen && asked.Done && asked.Answer.StartsWith("先用二分", StringComparison.Ordinal),
                        "answering finishes the question and keeps the answer");
                    Frame("drag-17-answered.png");

                    Store.Flush();
                    var reread = new Store(store.Directory);
                    reread.Load();
                    var same = reread.Ideas.FirstOrDefault(i => i.Time == asked.Time && i.Text == asked.Text);
                    Check(same != null && same.IsQuestion && same.Done && same.Answer == asked.Answer &&
                          same.AnsweredTime.Length == 14 && same.Starred == asked.Starred && same.Shelved == asked.Shelved,
                        "question, answer and flags survive a reload");

                    if (Container(0)?.DataContext is Idea later)
                    {
                        later.IsQuestion = true;
                        CompleteIdea(later);
                        await Wait(300);
                        AnswerBox.Text = "还没想好";
                        CloseAnswerSheet(true, null);
                        await Wait(300);
                        Check(!AnswerOpen && !later.Done && !later.HasAnswer, "Esc closes the sheet without finishing or saving");
                        CompleteIdea(later);
                        await Wait(300);
                        Check(AnswerBox.Text == "还没想好", "the draft is back when the sheet opens again");
                        SubmitAnswer(false);
                        await Wait(400);
                        Check(!AnswerOpen && !later.Done && later.Answer == "还没想好", "先存回答 keeps the answer, the record stays open");
                        Frame("drag-18-saved-answer.png");
                    }
                    else Check(false, "a second open card for the draft test");
                }
                else Check(false, "an open card to turn into a question");
            }
            catch (Exception error)
            {
                failures++;
                log.AppendLine("FAIL exception " + error);
            }
            log.Insert(0, (failures == 0 ? "ALL PASS" : failures + " FAILED") + Environment.NewLine);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "drag-selftest.txt"), log.ToString());
        }

        private void CancelDrag()
        {
            if (dragGhost == null)
            {
                CancelLongPress();
                return;
            }
            draggingCard = false;
            dragWatchdog.Stop();
            ReleaseMouseCapture();
            RestoreCard(dragItem);
            FinishCardDrag();
        }

        /// <summary>The card is drawn into the tray: shrink, tilt, fade into the slot.</summary>
        private void FlyIntoBin()
        {
            var target = new Point(binBounds.Left + binBounds.Width / 2,
                                   binBounds.Top + binBounds.Height * 0.34);
            var w = dragGhost.Width;
            var h = dragGhost.Height;

            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            ghostLift?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            ghostLift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            ghostFollow?.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target.X - w / 2, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ghostFollow?.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(target.Y - h / 2, duration) { EasingFunction = ease });
            ghostScale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.06, duration) { EasingFunction = ease });
            ghostScale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.06, duration) { EasingFunction = ease });
            ghostSpin?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(7, duration) { EasingFunction = ease });
            dragGhost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
                {
                    BeginTime = TimeSpan.FromMilliseconds(130)
                });

            BinSwallow();
            After(280, CommitShelve);
            After(420, FinishCardDrag);
        }

        private void BinSwallow()
        {
            var squash = new DoubleAnimationUsingKeyFrames();
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.09, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
                new CubicEase { EasingMode = EasingMode.EaseIn }));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            BinScale.BeginAnimation(ScaleTransform.ScaleXProperty, squash);
            BinScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash.Clone());

            var ripple = new System.Windows.Shapes.Ellipse
            {
                Width = 150, Height = 150,
                Stroke = BinInkIdle, StrokeThickness = 2.5, Opacity = 0.7,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.35, 0.35)
            };
            var center = new Point(binBounds.Left + binBounds.Width / 2,
                                   binBounds.Top + binBounds.Height * 0.34);
            Canvas.SetLeft(ripple, center.X - 75);
            Canvas.SetTop(ripple, center.Y - 75);
            DragCanvas.Children.Add(ripple);

            var pulse = new ScaleTransform(0.35, 0.35);
            ripple.RenderTransform = pulse;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            pulse.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.35, 1.45, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            pulse.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.35, 1.45, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, TimeSpan.FromMilliseconds(420)));
            After(440, () => DragCanvas.Children.Remove(ripple));
        }

        private void FinishCardDrag()
        {
            if (dragGhost != null)
                DragCanvas.Children.Remove(dragGhost);
            dragGhost = null;
            ghostScale = null;
            ghostSpin = null;
            ghostLift = null;
            ghostFollow = null;
            dragWatchdog.Stop();
            pressIdea = null;
            pressItem = null;
            pressArmed = false;
            if (dragItem != null)
            {
                RestoreCard(dragItem);
                dragItem.RenderTransform = null;
                dragItem = null;
            }
            dragIdea = null;
            binArmed = false;
            var hovered = hoverTagChip;
            hoverTagChip = null;
            hoverTag = null;
            ApplyChipColors(hovered);
            if (hovered != null)
                AnimateChipScale(hovered, 1, 160, new CubicEase { EasingMode = EasingMode.EaseOut });
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            FadeLayerOut();
        }

        private void FadeLayerOut()
        {
            if (DragLayer.Opacity <= 0.001)
                return;
            DragLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(DragLayer.Opacity, 0, TimeSpan.FromMilliseconds(150)));
            After(180, () =>
            {
                if (draggingCard)
                    return;
                DragLayer.Opacity = 0;
                DragLayer.BeginAnimation(OpacityProperty, null);
                DragCanvas.Children.Clear();
            });
        }

        private void After(double milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        /// <summary>Walks up from anything the mouse landed on, including Runs inside a TextBlock.</summary>
        private static T FindParent<T>(DependencyObject start) where T : DependencyObject
        {
            var current = start;
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        private void CommitShelve()
        {
            var idea = dragIdea;
            if (idea == null || idea.Done)
                return;
            // Put the card back before the list is rebuilt, or a recycled container
            // would inherit the dimmed look.
            if (dragItem != null)
            {
                dragItem.Opacity = 1;
                dragItem.RenderTransform = null;
            }
            idea.Shelved = !idea.Shelved;
            store.Save();
            Refresh();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            TryRegisterHotkey();
        }

        /// <summary>The global hotkey; "Hotkey=" in layout.ini, Alt+Q by default.</summary>
        private Hotkey hotkey;

        private void TryRegisterHotkey()
        {
            // A previous copy may still be shutting down and holding the key.
            // Keep retrying for a few seconds instead of silently losing it.
            hotkey ??= Hotkey.Parse(store.LoadHotkey());
            var handle = new WindowInteropHelper(this).Handle;
            if (RegisterHotKey(handle, HotkeyId, hotkey.Modifiers | Hotkey.NoRepeat, hotkey.VirtualKey))
            {
                return;
            }
            hotkeyAttempts++;
            if (hotkeyAttempts >= 12)
            {
                tray?.ShowBalloonTip(4000, "FastNote",
                    hotkey.Text + " 已被其他程序占用。可以在 layout.ini 里改 Hotkey，或从托盘图标打开。",
                    WinForms.ToolTipIcon.Warning);
                return;
            }
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            retry.Tick += (s, e) =>
            {
                retry.Stop();
                TryRegisterHotkey();
            };
            retry.Start();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                ToggleVisibility();
                handled = true;
            }
            else if (msg == WmGetMinMaxInfo)
            {
                ClampToWorkArea(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            // A frameless WindowChrome window maximises to its outer frame (the
            // resize border), which lands a few pixels off screen and under the
            // taskbar. Pin the maximised bounds to the monitor work area.
            var monitor = MonitorFromWindow(hwnd, 2);
            if (monitor == IntPtr.Zero)
                return;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfoW(monitor, ref info))
                return;
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            bounds.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            bounds.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            bounds.MaxSize.X = info.Work.Right - info.Work.Left;
            bounds.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            bounds.MaxTrackSize = bounds.MaxSize;
            Marshal.StructureToPtr(bounds, lParam, true);
        }

        private void ToggleVisibility()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                Composer.Focus();
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!ReallyExit)
            {
                // The close button parks the window in the tray so the hotkey keeps working.
                e.Cancel = true;
                SaveDraft();
                SaveLayoutNow();
                if (tray == null)
                    CreateTray();   // hidden before the idle callback ran
                Hide();
                return;
            }
            SaveDraft();
            SaveLayoutNow();
            Store.Flush();
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            if (tray != null)
                tray.Visible = false;
        }

        /// <summary>
        /// The real exit, shared by the tray menu and "FastNote.exe --exit": saves
        /// the draft and layout, flushes the store and removes the tray icon. An
        /// unsaved edit of an existing record still asks first, exactly like the menu.
        /// </summary>
        public void RequestExit()
        {
            if (ReallyExit)
                return;
            if (editingIdea != null && composerEditChanged && !IsVisible)
            {
                Show();
                Activate();
            }
            if (!ResolveComposerEdit())
                return;
            ReallyExit = true;
            Close();
            Application.Current.Shutdown();
        }

        private void CreateTray()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("打开 FastNote", null, (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            menu.Items.Add("关于", null, (s, e) => ShowAbout());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => RequestExit());
            tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "FastNote",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
        }

        private void ShowAbout()
        {
            var about = new AboutWindow(store.Directory, (hotkey ?? Hotkey.Parse(null)).Text);
            if (IsVisible)
            {
                about.Owner = this;
                about.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            about.ShowDialog();
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                    return icon;
            }
            catch (Exception)
            {
                // fall through to the stock icon
            }
            return System.Drawing.SystemIcons.Application;
        }

        // ------------------------------------------------------------ caption

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // The close button parks the window in the tray, like the system one.
            Close();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            var maximized = WindowState == WindowState.Maximized;
            MaxGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
            RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PersistRatio()
        {
            if (applyingLayout || !IsLoaded)
                return;
            layoutSaveTimer.Stop();
            layoutSaveTimer.Start();
        }

        private void SaveLayoutNow()
        {
            layoutSaveTimer.Stop();
            if (!IsLoaded)
                return;
            var total = OpenColumn.ActualWidth + DoneColumn.ActualWidth;
            if (total <= 1)
                return;
            store.SaveSettings(OpenColumn.ActualWidth / total, Topmost, ComposerRow.ActualHeight, textScale);
        }

        // ---------------------------------------------------------------- data

        // ---------------------------------------------------------------- tags

        private void UpdateTagStrip(int openTotal)
        {
            if (TagStripMatches(tagBuckets))
            {
                SetChipCount(allTagCountText, openTotal);
                foreach (var bucket in tagBuckets)
                    SetChipCount(tagCountText[bucket.Name], bucket.Open);

                if (paintedTag != currentTag)
                {
                    paintedTag = currentTag;
                    foreach (var chip in tagChips)
                        ApplyChipColors(chip);
                }
                return;
            }

            paintedTag = currentTag;
            TagStrip.Children.Clear();
            tagChips.Clear();
            tagCountText.Clear();
            allTagCountText = null;
            hoverTagChip = null;
            hoverTag = null;

            TagStrip.Children.Add(BuildTagChip("全部", openTotal, null, null, true));

            // The scope control and the buckets are two different things; a hairline
            // keeps them from reading as one long row of the same pill.
            TagStrip.Children.Add(new Border
            {
                Width = 1,
                Height = 15,
                Background = FrozenBrush("#DADEE5"),
                Margin = new Thickness(6, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            foreach (var bucket in tagBuckets)
                TagStrip.Children.Add(BuildTagChip(bucket.Name, bucket.Open, bucket.Name, bucket.Name, false));

            TagStrip.Children.Add(BuildNewTagChip());
        }

        /// <summary>True when the chips on screen already are these buckets, in order.</summary>
        private bool TagStripMatches(List<TagBucket> buckets)
        {
            if (tagChips.Count != buckets.Count + 1 || TagStrip.Children.Count != buckets.Count + 3)
                return false;
            if (tagChips[0].Tag != null)
                return false;
            for (var i = 0; i < buckets.Count; i++)
            {
                // Index 0 is 全部, so the buckets start at one.
                if (!string.Equals(tagChips[i + 1].Tag as string, buckets[i].Name, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>Writes the number only when it moved, so no redundant layout pass.</summary>
        private static void SetChipCount(TextBlock text, int count)
        {
            var next = count.ToString();
            if (!string.Equals(text.Text, next, StringComparison.Ordinal))
                text.Text = next;
        }

        // ------------------------------------------------------------- labels

        private void UpdateLabelStrip(Dictionary<string, TagBucket> labels)
        {
            var hasLabels = labels.Count > 0;
            LabelToggle.Visibility = hasLabels ? Visibility.Visible : Visibility.Collapsed;
            LabelToggleText.Text = currentLabel == null ? "摘要" : "摘要 · " + currentLabel;
            LabelToggle.ToolTip = currentLabel == null ? "按摘要筛选" : "摘要：" + currentLabel;
            var showLabels = hasLabels && LabelToggle.IsChecked == true;
            LabelBar.Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
            if (!showLabels)
                return;

            var buckets = labels.Values.OrderBy(bucket => bucket.First, StringComparer.Ordinal).ToList();
            if (labelChips.Select(chip => (string)chip.Tag).SequenceEqual(buckets.Select(bucket => bucket.Name)))
            {
                for (var i = 0; i < buckets.Count; i++)
                {
                    var chip = labelChips[i];
                    var content = (StackPanel)chip.Content;
                    SetChipCount((TextBlock)content.Children[1], buckets[i].Open);
                    if (paintedLabel != currentLabel)
                        ApplyLabelChipColors(chip);
                }
                paintedLabel = currentLabel;
                return;
            }

            paintedLabel = currentLabel;
            LabelStrip.Children.Clear();
            labelChips.Clear();
            foreach (var bucket in buckets)
                LabelStrip.Children.Add(BuildLabelChip(bucket.Name, bucket.Open));
        }

        private void LabelToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                Refresh();
        }

        private Button BuildLabelChip(string label, int count)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var number = new TextBlock
            {
                Text = count.ToString(),
                FontSize = 11,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(text);
            content.Children.Add(number);

            var chip = new Button
            {
                Content = content,
                Style = (Style)FindResource("TagChipButton"),
                Tag = label,
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "只看「" + label + "」"
            };
            chip.Click += (s2, e2) =>
            {
                currentLabel = currentLabel == label ? null : label;
                Refresh();
            };
            labelChips.Add(chip);
            ApplyLabelChipColors(chip);
            return chip;
        }

        private void ApplyLabelChipColors(Button chip)
        {
            var active = currentLabel == (string)chip.Tag;
            var content = (StackPanel)chip.Content;
            chip.Background = active ? (Brush)FindResource("AccentSoft") : FrozenBrush("#ECEEF2");
            chip.BorderBrush = active ? (Brush)FindResource("Accent") : Brushes.Transparent;
            ((TextBlock)content.Children[0]).Foreground = (Brush)FindResource(active ? "AccentDeep" : "Ink2");
            ((TextBlock)content.Children[1]).Foreground = (Brush)FindResource(active ? "AccentDeep" : "Muted");
        }

        private void AssignLabel(Idea idea, string label)
        {
            if (idea == null)
                return;
            var next = label ?? "";
            if (string.Equals(idea.Label, next, StringComparison.Ordinal))
                return;
            idea.Label = next;
            store.Save();
            Refresh();
        }

        /// <summary>Gists already in use around this record, so the menu is short.</summary>
        private List<string> NearbyLabels(Idea idea)
        {
            var scope = idea.HasTag
                ? store.Ideas.Where(i => string.Equals(i.Tag, idea.Tag, StringComparison.Ordinal))
                : store.Ideas;
            return scope.Where(i => i.HasLabel)
                .GroupBy(i => i.Label, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
        }

        /// <summary>
        /// The saved tag list first (so chips keep the order you made them in), then
        /// any tag that only exists on a record (hand-edited file, older build).
        /// </summary>
        private List<TagBucket> OrderTagBuckets(Dictionary<string, TagBucket> found)
        {
            var ordered = new List<TagBucket>();
            foreach (var name in tagNames)
            {
                ordered.Add(found.Remove(name, out var bucket) ? bucket : new TagBucket { Name = name });
            }
            ordered.AddRange(found.Values.OrderBy(b => b.First, StringComparer.Ordinal));
            return ordered;
        }

        private static void CountBucket(Dictionary<string, TagBucket> buckets, string name, string time, bool active)
        {
            if (!buckets.TryGetValue(name, out var bucket))
            {
                bucket = new TagBucket { Name = name, First = time };
                buckets.Add(name, bucket);
            }
            if (string.CompareOrdinal(time, bucket.First) < 0)
                bucket.First = time;
            if (active)
                bucket.Open++;
        }

        private sealed class TagBucket
        {
            public string Name;
            public string First;
            public int Open;
        }

        private Button BuildTagChip(string label, int count, string tagValue, string dropTag, bool primary)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontFamily = UiFonts.Ui,
                VerticalAlignment = VerticalAlignment.Center
            };
            var number = new TextBlock
            {
                Text = count.ToString(),
                FontSize = 12,
                FontFamily = UiFonts.Ui,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(text);
            content.Children.Add(number);

            var chip = new Button
            {
                Content = content,
                Style = (Style)FindResource("TagChipButton"),
                Tag = tagValue,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = tagValue == null
                    ? "全部"
                    : "只看「" + tagValue + "」"
            };
            chip.Click += (s2, e2) => SetTag(chip.Tag as string);
            chip.MouseEnter += (s2, e2) => ApplyChipColors(chip);
            chip.MouseLeave += (s2, e2) => ApplyChipColors(chip);
            if (dropTag != null)
                chip.ContextMenu = BuildTagChipMenu(dropTag);

            if (primary)
                allTagCountText = number;
            else
                tagCountText[tagValue] = number;

            tagChips.Add(chip);
            ApplyChipColors(chip);
            return chip;
        }

        private Button BuildNewTagChip()
        {
            var text = new TextBlock { Text = "+ 标签", FontSize = 13, Foreground = (Brush)FindResource("Muted") };
            var hover = FrozenBrush("#E8EAEF");
            var chip = new Button
            {
                Content = text,
                Style = (Style)FindResource("TagChipButton"),
                Uid = "new",
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "新建标签"
            };
            chip.MouseEnter += (s2, e2) => chip.Background = hover;
            chip.MouseLeave += (s2, e2) => chip.Background = Brushes.Transparent;
            chip.Click += (s2, e2) =>
            {
                var name = PromptTag("新建标签", "");
                if (name != null)
                    CreateTag(name);
            };
            return chip;
        }

        private ContextMenu BuildTagChipMenu(string tag)
        {
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名标签…" };
            rename.Click += (s2, e2) =>
            {
                var name = PromptTag("重命名标签", tag);
                if (name != null && !string.Equals(name, tag, StringComparison.Ordinal))
                    RenameTag(tag, name);
            };
            var remove = new MenuItem { Header = "删除标签（事项保留）" };
            remove.Click += (s2, e2) => DeleteTag(tag);
            menu.Items.Add(rename);
            menu.Items.Add(remove);
            return menu;
        }

        /// <summary>Idle / hovered / current: three looks, driven from one place.</summary>
        private void ApplyChipColors(Button chip)
        {
            if (chip == null)
                return;
            if (chip.Uid == "new")
                return;
            var primary = chip.Tag == null;
            var tagValue = chip.Tag as string;
            var active = primary ? currentTag == null : string.Equals(tagValue, currentTag, StringComparison.Ordinal);
            var hot = ReferenceEquals(chip, hoverTagChip) || chip.IsMouseOver;

            Brush background;
            Brush ink;
            Brush numberInk;
            Brush edge;
            var dropTarget = ReferenceEquals(chip, hoverTagChip);
            if (dropTarget)
            {
                // A card is being dragged over this chip: make the target obvious.
                background = (Brush)FindResource("AccentSoft");
                ink = (Brush)FindResource("AccentDeep");
                numberInk = ink;
                edge = (Brush)FindResource("Accent");
            }
            else if (active)
            {
                // Where you are standing: a solid dark pill.
                background = (Brush)FindResource("Ink");
                ink = Brushes.White;
                numberInk = FrozenBrush("#B7BECA");
                edge = background;
            }
            else if (hot)
            {
                background = FrozenBrush("#E8EAEF");
                ink = (Brush)FindResource("Ink");
                numberInk = (Brush)FindResource("Muted");
                edge = Brushes.Transparent;
            }
            else
            {
                background = Brushes.Transparent;
                ink = (Brush)FindResource("Ink2");
                numberInk = (Brush)FindResource("Muted");
                edge = Brushes.Transparent;
            }

            chip.Background = background;
            chip.BorderBrush = edge;
            if (chip.Content is StackPanel panel && panel.Children.Count >= 2)
            {
                if (panel.Children[0] is TextBlock label)
                    label.Foreground = ink;
                if (panel.Children[1] is TextBlock number)
                    number.Foreground = numberInk;
            }
        }

        private string PromptTag(string title, string initial)
        {
            var dialog = new TagWindow(title, initial, textScale) { Owner = this };
            var result = dialog.ShowDialog();
            return result == true ? dialog.TagName : null;
        }

        private void CreateTag(string name)
        {
            // Created but not entered: an empty bucket would hide the very records
            // you are about to move into it.
            if (tagBuckets.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal)))
            {
                SetTag(name);
                return;
            }
            tagNames.Add(name);
            store.SaveTags(tagNames);
            Refresh();
            var chip = tagChips.FirstOrDefault(c => string.Equals(c.Tag as string, name, StringComparison.Ordinal));
            if (chip != null)
                PulseChip(chip);
        }

        private void RenameTag(string from, string to)
        {
            var index = tagNames.IndexOf(from);
            if (index >= 0)
            {
                if (tagNames.Contains(to))
                    tagNames.RemoveAt(index);
                else
                    tagNames[index] = to;
            }
            else if (!tagNames.Contains(to))
                tagNames.Add(to);
            store.SaveTags(tagNames);

            foreach (var idea in store.Ideas.Where(i => string.Equals(i.Tag, from, StringComparison.Ordinal)))
                idea.Tag = to;
            if (string.Equals(currentTag, from, StringComparison.Ordinal))
                currentTag = to;
            store.Save();
            Refresh();
        }

        private void DeleteTag(string tag)
        {
            if (tagNames.RemoveAll(n => string.Equals(n, tag, StringComparison.Ordinal)) > 0)
                store.SaveTags(tagNames);

            foreach (var idea in store.Ideas.Where(i => string.Equals(i.Tag, tag, StringComparison.Ordinal)))
                idea.Tag = "";
            if (string.Equals(currentTag, tag, StringComparison.Ordinal))
                currentTag = null;
            store.Save();
            Refresh();
        }

        private void SetTag(string tag)
        {
            currentTag = string.IsNullOrEmpty(tag) ? null : tag;
            currentLabel = null;
            Refresh();
        }

        private void AssignTag(Idea idea, string tag)
        {
            if (idea == null)
                return;
            var next = tag ?? "";
            if (string.Equals(idea.Tag, next, StringComparison.Ordinal))
                return;
            idea.Tag = next;
            store.Save();
            Refresh();
        }

        private static bool Matches(Idea idea, string query)
        {
            if (idea.Text != null && idea.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // Typing a tag name or a gist is how you jump without the strips.
            if (idea.HasTag && idea.Tag.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (idea.HasAnswer && idea.Answer.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return idea.HasLabel && idea.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PulseChip(Button chip)
        {
            if (chip == null)
                return;
            var scale = ChipScale(chip);
            var bounce = new DoubleAnimationUsingKeyFrames();
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)),
                new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce.Clone());
        }

        private void CardMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = sender as ContextMenu;
            var idea = menu?.DataContext as Idea;
            if (menu != null && idea != null)
            {
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (Equals(item.Tag, "starmenu"))
                        item.Header = idea.Starred ? "取消星标" : "加星标";
                    else if (Equals(item.Tag, "shelvemenu"))
                    {
                        item.Visibility = idea.Done ? Visibility.Collapsed : Visibility.Visible;
                        item.Header = idea.Shelved ? "取消搁置" : "搁置";
                    }
                    else if (Equals(item.Tag, "donemenu"))
                        item.Header = idea.Done ? "恢复为未完成" : idea.IsQuestion ? "回答并完成…" : "完成";
                    else if (Equals(item.Tag, "questionmenu"))
                        item.Header = idea.IsQuestion ? "取消问题" : "设为问题";
                    else if (Equals(item.Tag, "answermenu"))
                    {
                        item.Visibility = idea.IsQuestion || idea.HasAnswer ? Visibility.Visible : Visibility.Collapsed;
                        item.Header = idea.HasAnswer ? "修改回答…" : "写回答…";
                    }
                }
            }
            var submenu = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Tag, "tagmenu"));
            if (idea == null || submenu == null)
                return;

            submenu.Items.Clear();
            var buckets = tagBuckets;
            foreach (var bucket in buckets)
            {
                var name = bucket.Name;
                var item = new MenuItem
                {
                    Header = "#" + name,
                    IsCheckable = true,
                    IsChecked = string.Equals(idea.Tag, name, StringComparison.Ordinal)
                };
                item.Click += (s2, e2) => AssignTag(idea, name);
                submenu.Items.Add(item);
            }

            if (buckets.Count > 0)
                submenu.Items.Add(new Separator());

            var create = new MenuItem { Header = "新建标签…" };
            create.Click += (s2, e2) =>
            {
                var name = PromptTag("新建标签", "");
                if (name != null)
                    AssignTag(idea, name);
            };
            submenu.Items.Add(create);

            if (idea.HasTag)
            {
                var leave = new MenuItem { Header = "移出标签" };
                leave.Click += (s2, e2) => AssignTag(idea, "");
                submenu.Items.Add(leave);
            }

            var labelMenu = menu.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Tag, "labelmenu"));
            if (labelMenu == null)
                return;
            FillLabelMenu(labelMenu.Items, idea);
        }

        /// <summary>Shared by the right-click submenu and the card's 摘要 button.</summary>
        private void FillLabelMenu(System.Windows.Controls.ItemCollection items, Idea idea)
        {
            items.Clear();
            var labels = NearbyLabels(idea);
            foreach (var name in labels)
            {
                var item = new MenuItem
                {
                    Header = name,
                    IsCheckable = true,
                    IsChecked = string.Equals(idea.Label, name, StringComparison.Ordinal)
                };
                item.Click += (s2, e2) => AssignLabel(idea, name);
                items.Add(item);
            }

            if (labels.Count > 0)
                items.Add(new Separator());

            var newLabel = new MenuItem { Header = "新建摘要…" };
            newLabel.Click += (s2, e2) =>
            {
                var name = PromptTag("新建摘要", idea.Label ?? "");
                if (name != null)
                    AssignLabel(idea, name);
            };
            items.Add(newLabel);

            if (idea.HasLabel)
            {
                var clear = new MenuItem { Header = "清除摘要" };
                clear.Click += (s2, e2) => AssignLabel(idea, "");
                items.Add(clear);
            }
        }

        /// <summary>The card's 摘要 button: same picker, one click, no right-click.</summary>
        private void CardLabelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not Idea idea)
                return;
            var menu = new ContextMenu();
            FillLabelMenu(menu.Items, idea);
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Updates the headers from the same records that feed the card lists.
        /// </summary>
        private void UpdateScopeCounters(string query, bool starOnly, bool shelvedOnly,
            int totalActive, int totalShelved, int totalDone, int visibleOpen, int visibleDone)
        {
            var filtering = query.Length > 0 || starOnly;
            var openPool = shelvedOnly ? totalShelved
                : query.Length > 0 ? totalActive + totalShelved : totalActive;

            OpenCount.Text = filtering || openWeek != null
                ? string.Format("{0} / {1}", visibleOpen, openPool)
                : openPool.ToString();
            DoneCount.Text = filtering || doneWeek != null
                ? string.Format("{0} / {1}", visibleDone, totalDone)
                : totalDone.ToString();

            OpenHeaderText.Text = shelvedOnly ? "搁置" : "未完成";
            OpenHeaderText.Foreground = (System.Windows.Media.Brush)FindResource(shelvedOnly ? "ShelveInk" : "Ink");
            OpenCountPill.Background = (System.Windows.Media.Brush)FindResource(shelvedOnly ? "ShelvePillBg" : "OpenPillBg");
            OpenCount.Foreground = (System.Windows.Media.Brush)FindResource(shelvedOnly ? "ShelveInk" : "Muted");

            OpenEmpty.Text = query.Length > 0 ? "没有匹配的事项"
                : openWeek != null ? "这一周没有事项"
                : currentLabel != null ? "「" + currentLabel + "」下没有未完成的事项"
                : currentTag != null ? "「" + currentTag + "」里没有未完成的事项"
                : shelvedOnly ? "没有搁置的事项"
                : starOnly ? "没有星标事项"
                : "没有未完成的事项";
            DoneEmpty.Text = query.Length > 0 ? "没有匹配的事项"
                : doneWeek != null ? "这一周没有事项"
                : currentLabel != null ? "「" + currentLabel + "」下没有完成的事项"
                : currentTag != null ? "「" + currentTag + "」里没有完成的事项"
                : starOnly ? "没有星标事项"
                : "还没有完成的事项";
            OpenEmpty.Visibility = visibleOpen == 0 ? Visibility.Visible : Visibility.Collapsed;
            DoneEmpty.Visibility = visibleDone == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Refresh()
        {
            searchTimer.Stop();
            var query = (SearchBox.Text ?? "").Trim();
            var starOnly = StarFilter.IsChecked == true;
            var shelvedOnly = ShelveFilter.IsChecked == true;
            var groupOpen = OpenWeekToggle.IsChecked == true;
            var groupDone = DoneWeekToggle.IsChecked == true;

            if (currentLabel != null && !store.Ideas.Any(i =>
                i.Label == currentLabel && (currentTag == null || i.Tag == currentTag)))
                currentLabel = null;
            if (!groupOpen || (openWeek != null && !store.Ideas.Any(i => !i.Done && i.WeekStart == openWeek.Start)))
                openWeek = null;
            if (!groupDone || (doneWeek != null && !store.Ideas.Any(i => i.Done && i.WeekStart == doneWeek.Start)))
                doneWeek = null;

            var tags = new Dictionary<string, TagBucket>(StringComparer.Ordinal);
            var labels = new Dictionary<string, TagBucket>(StringComparer.Ordinal);
            var visibleOpen = new List<Idea>();
            var visibleDone = new List<Idea>();
            int allActive = 0, allShelved = 0, starred = 0;
            int totalActive = 0, totalShelved = 0, totalDone = 0;

            foreach (var idea in store.Ideas)
            {
                var active = !idea.Done && !idea.Shelved;
                if (active) allActive++;
                if (!idea.Done && idea.Shelved) allShelved++;
                if (idea.Starred) starred++;
                if (idea.HasTag)
                    CountBucket(tags, idea.Tag, idea.Time, active);

                if (currentTag != null && idea.Tag != currentTag)
                    continue;
                if (idea.HasLabel)
                    CountBucket(labels, idea.Label, idea.Time, active);
                if (currentLabel != null && idea.Label != currentLabel)
                    continue;

                if (idea.Done) totalDone++;
                else if (idea.Shelved) totalShelved++;
                else totalActive++;

                if ((starOnly && !idea.Starred) || (query.Length > 0 && !Matches(idea, query)))
                    continue;
                if (!idea.Done && (shelvedOnly ? !idea.Shelved : query.Length == 0 && idea.Shelved))
                    continue;
                var week = idea.Done ? doneWeek : openWeek;
                if (week != null && idea.WeekStart != week.Start)
                    continue;
                (idea.Done ? visibleDone : visibleOpen).Add(idea);
            }

            tagBuckets = OrderTagBuckets(tags);
            UpdateTagStrip(allActive);
            UpdateLabelStrip(labels);
            UpdateFilterCounts(starred, allShelved);
            UpdateScopeCounters(query, starOnly, shelvedOnly, totalActive, totalShelved, totalDone,
                visibleOpen.Count, visibleDone.Count);
            UpdateComposerPlaceholder();
            ShowWeekBar(true, openWeek, visibleOpen.Count);
            ShowWeekBar(false, doneWeek, visibleDone.Count);

            UpdateColumn(OpenList, openView, openWeeks, visibleOpen, groupOpen && openWeek == null);
            UpdateColumn(DoneList, doneView, doneWeeks, visibleDone, groupDone && doneWeek == null);

            SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActionState();
        }

        private void UpdateColumn(ListBox list, IdeaList source, Dictionary<DateTime, WeekSummary> weeks,
            List<Idea> visible, bool grouped)
        {
            var selected = list.SelectedItem as Idea;
            var mode = grouped ? ListMode.Grouped : ListMode.Cards;
            if (!listModes.TryGetValue(list, out var current) || current != mode)
            {
                source.Clear();
                ApplyListMode(list, mode);
                var view = CollectionViewSource.GetDefaultView(source);
                using (view.DeferRefresh())
                {
                    view.GroupDescriptions.Clear();
                    if (grouped)
                        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Idea.Week)));
                }
            }

            var items = grouped ? WeekPreview(visible, weeks) : visible;
            source.Update(items);
            if (selected != null && list.SelectedItem != selected && source.Contains(selected))
                list.SelectedItem = selected;
        }

        private static List<Idea> WeekPreview(List<Idea> visible, Dictionary<DateTime, WeekSummary> weeks)
        {
            var preview = new List<Idea>();
            var groups = visible.GroupBy(idea => idea.WeekStart);
            var shown = new HashSet<DateTime>();
            foreach (var group in groups)
            {
                var start = group.Key;
                shown.Add(start);
                if (!weeks.TryGetValue(start, out var summary))
                {
                    summary = new WeekSummary(start, group.Count(), WeekPreviewCount);
                    weeks.Add(start, summary);
                }
                else
                    summary.Total = group.Count();

                foreach (var idea in group.Take(WeekPreviewCount))
                {
                    idea.Week = summary;
                    preview.Add(idea);
                }
            }
            foreach (var start in weeks.Keys.Where(start => !shown.Contains(start)).ToArray())
                weeks.Remove(start);
            return preview;
        }

        /// <summary>
        /// A drilled week takes over the column header (same row, same height), so
        /// the other column stays exactly where it was.
        /// </summary>
        private void ShowWeekBar(bool openColumn, WeekSummary week, int count)
        {
            var header = openColumn ? OpenHeaderRow : (FrameworkElement)DoneHeaderRow;
            var bar = openColumn ? OpenWeekBar : DoneWeekBar;
            var title = openColumn ? OpenWeekTitle : DoneWeekTitle;
            var tag = openColumn ? OpenWeekTag : DoneWeekTag;
            var tagBorder = openColumn ? OpenWeekTagBorder : DoneWeekTagBorder;
            var counter = openColumn ? OpenWeekCount : DoneWeekCount;

            var drilled = week != null;
            header.Visibility = drilled ? Visibility.Collapsed : Visibility.Visible;
            bar.Visibility = drilled ? Visibility.Visible : Visibility.Collapsed;
            if (!drilled)
                return;

            title.Text = week.Label;
            tag.Text = week.Tag;
            tagBorder.Visibility = week.Tag.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            counter.Text = count + " 条";
        }

        private void UpdateComposerHint()
        {
            // The placeholder steps aside while typing, so the caret never sits
            // on top of the hint text.
            var empty = string.IsNullOrEmpty(RichDocument.Text(Composer.Document)) && RichDocument.Images(Composer.Document).Count == 0;
            ComposerHint.Visibility = empty
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateComposerPlaceholder()
        {
            ComposerHint.Text = editingIdea != null ? "修改事项…"
                : currentTag == null ? "记点什么…"
                : "记到「" + currentTag + "」…";
        }

        /// <summary>
        /// Grouped and ungrouped columns need different item templates; a drilled
        /// week deliberately keeps the normal card so it looks like the list you
        /// just came from.
        /// </summary>
        private void ApplyListMode(ListBox list, ListMode mode)
        {
            if (listModes.TryGetValue(list, out var current) && current == mode && list.ItemTemplate != null)
                return;

            var grouped = mode == ListMode.Grouped;
            var doneRows = !grouped && list == DoneList;
            list.ItemContainerStyle = (Style)FindResource(grouped ? "CompactCardItem" : doneRows ? "DoneRowItem" : "CardItem");
            list.ItemTemplate = (DataTemplate)FindResource(grouped ? "CompactCardTemplate" : doneRows ? "DoneRowTemplate" : "CardTemplate");
            list.GroupStyle.Clear();
            if (grouped)
                list.GroupStyle.Add((GroupStyle)FindResource("WeekGroupStyle"));
            listModes[list] = mode;
        }

        private void WeekOpen_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!((sender as FrameworkElement)?.DataContext is CollectionViewGroup group) ||
                !(group.Name is WeekSummary week))
                return;

            var done = FindAncestor<ListBox>(sender as DependencyObject) == DoneList;
            if (done) doneWeek = week;
            else openWeek = week;
            Refresh();

            // The week opens inside its own column, so the other column keeps
            // showing whatever it was showing.
            var list = done ? DoneList : OpenList;
            list.SelectedItem = null;
            list.Focus();
            if (list.Items.Count > 0)
                list.ScrollIntoView(list.Items[0]);
        }

        private void WeekBack_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, DoneWeekBack)) doneWeek = null;
            else openWeek = null;
            Refresh();
        }

        private void CloseDrill()
        {
            openWeek = null;
            doneWeek = null;
            Refresh();
        }

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            var current = start;
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void WeekMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
                return;
            // Each column folds on its own: the left one by week, the right one by
            // week - either, both or neither.
            if (OpenWeekToggle.IsChecked != true) openWeek = null;
            if (DoneWeekToggle.IsChecked != true) doneWeek = null;
            Refresh();
        }

        private void ShelveFilter_Toggled(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                Refresh();
        }

        private void UpdateFilterCounts(int starred, int shelved)
        {
            // Both pills double as entry points, so they always show how many
            // records are waiting behind them.
            StarCount.Text = starred.ToString();
            StarFilter.ToolTip = starred == 0
                ? "没有星标事项"
                : string.Format("只看星标（{0} 条）· Ctrl+Shift+S", starred);

            ShelveCount.Text = shelved.ToString();
            ShelveFilter.ToolTip = shelved == 0
                ? "没有搁置的事项"
                : string.Format("只看搁置（{0} 条）", shelved);
        }

        private void UpdateActionState()
        {
            bool hasOpen = OpenList.SelectedItem != null;
            bool hasDone = DoneList.SelectedItem != null;
            CompleteButton.IsEnabled = hasOpen;
            EditOpenButton.IsEnabled = hasOpen;
            CopyOpenButton.IsEnabled = hasOpen;
            DeleteOpenButton.IsEnabled = hasOpen;
            RestoreButton.IsEnabled = hasDone;
            CopyDoneButton.IsEnabled = hasDone;
            DeleteDoneButton.IsEnabled = hasDone;
        }

        private bool SubmitComposer()
        {
            if (editingIdea != null)
                return SaveComposerEdit();
            AddIdea();
            return true;
        }

        private void AddIdea()
        {
            var text = RichDocument.Text(Composer.Document).Trim();
            var richContent = RichDocument.Serialize(Composer.Document);
            if (text.Length == 0 && richContent == null)
            {
                Composer.Focus();
                return;
            }
            // Writing while standing in a tag files the note there.
            var idea = new Idea
            {
                Time = DateTimeStamp.Now(),
                Done = false,
                Text = text,
                RichContent = richContent,
                Tag = currentTag ?? "",
                Label = currentLabel ?? "",
                // "…？" reads as a question you will want to answer, not tick off.
                IsQuestion = Idea.LooksLikeQuestion(text)
            };
            store.Ideas.Insert(0, idea);
            store.Save();

            RichDocument.Load(Composer.Document, "", null);
            SaveDraft();
            Refresh();
            if (openView.Contains(idea))
            {
                OpenList.SelectedItem = idea;
                if (ScrollOffset(OpenList) > 0.5)
                    OpenList.ScrollIntoView(idea);
            }
            Composer.Focus();
        }

        private static double ScrollOffset(ListBox list)
        {
            var viewer = FindScrollViewer(list);
            return viewer?.VerticalOffset ?? 0;
        }

        private void Move(Idea idea, bool done)
        {
            if (idea == null)
                return;
            // Stay in the column you are working in: the record leaves, the one that
            // takes its place is selected (so Enter, Enter works down a list), and the
            // other column is not scrolled to chase it - an old record finished today
            // used to drag 已完成 all the way down.
            var from = done ? OpenList : DoneList;
            var index = from.Items.IndexOf(idea);
            idea.Done = done;
            // Finished records drop the shelve flag: the parking spot is only for
            // things that are still open.
            if (done) idea.Shelved = false;
            store.Save();
            Refresh();
            if (from.Items.Count > 0)
            {
                var next = from.Items[Math.Clamp(index < 0 ? 0 : index, 0, from.Items.Count - 1)];
                from.SelectedItem = next;
                from.UpdateLayout();
                if (from.ItemContainerGenerator.ContainerFromItem(next) is ListBoxItem nextItem && nextItem.IsVisible)
                    nextItem.Focus();
                else
                    from.Focus();
            }
            else
                from.Focus();
            UpdateActionState();
        }

        /// <summary>Parks an open record (or brings it back) without finishing it.</summary>
        private void ToggleShelve(Idea idea)
        {
            if (idea == null || idea.Done)
                return;
            idea.Shelved = !idea.Shelved;
            store.Save();
            Refresh();
            // Offer the card back when the current view still contains it, so a
            // mis-click is one key away from being undone.
            var host = OpenList;
            if (host.Items.Contains(idea))
            {
                host.SelectedItem = idea;
                host.ScrollIntoView(idea);
            }
            host.Focus();
        }

        private void Copy(Idea idea, Button feedbackButton)
        {
            if (idea == null)
                return;
            try
            {
                var document = new System.Windows.Documents.FlowDocument();
                RichDocument.Load(document, idea.Text, idea.RichContent);
                // A question is copied together with its answer.
                if (idea.HasAnswer)
                {
                    document.Blocks.Add(new Paragraph());
                    foreach (var line in ("答：" + idea.Answer.Trim()).Replace("\r\n", "\n").Split('\n'))
                        document.Blocks.Add(new Paragraph(new Run(line)));
                }
                RichDocument.Copy(document);
            }
            catch (Exception)
            {
                return;
            }
            if (feedbackButton != null)
            {
                ShowCopied(feedbackButton);
                feedbackTimer.Stop();
                feedbackTimer.Start();
            }
        }

        // Copy buttons are icons now: remember what each showed and put it back.
        private readonly Dictionary<Button, object> copyCaptions = new Dictionary<Button, object>();

        private void ShowCopied(Button button)
        {
            if (!copyCaptions.ContainsKey(button))
                copyCaptions[button] = button.Content;
            button.Content = new TextBlock { Text = "已复制", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }

        private void ResetCopyCaptions()
        {
            foreach (var pair in copyCaptions)
                pair.Key.Content = pair.Value;
            copyCaptions.Clear();
        }

        // ------------------------------------------------------------- handlers

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            SubmitComposer();
        }

        private void SaveDraft()
        {
            if (editingIdea != null || !draftTimer.IsEnabled)
                return;
            draftTimer.Stop();
            store.SaveDraft(RichDocument.Text(Composer.Document), RichDocument.Serialize(Composer.Document));
        }

        private void PasteIntoComposer()
        {
            try
            {
                if (Clipboard.ContainsData(System.Windows.DataFormats.XamlPackage))
                {
                    Composer.Focus();
                    Composer.Paste();
                }
                else if (Clipboard.ContainsImage())
                    RichDocument.InsertImage(Composer, Clipboard.GetImage());
                else if (Clipboard.ContainsFileDropList())
                {
                    foreach (string path in Clipboard.GetFileDropList())
                        RichDocument.InsertImage(Composer, RichDocument.ReadImage(path));
                }
                else
                {
                    Composer.Focus();
                    Composer.Paste();
                }
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show(this, "无法粘贴内容：" + error.Message, "粘贴", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e) => PasteIntoComposer();

        private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PasteIntoComposer();
                e.Handled = true;
            }
        }

        private void ImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图片", Multiselect = true,
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                foreach (var path in dialog.FileNames)
                    RichDocument.InsertImage(Composer, RichDocument.ReadImage(path));
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show(this, "无法添加图片：" + error.Message, "添加图片", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CopyComposerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(RichDocument.Text(Composer.Document)) && RichDocument.Images(Composer.Document).Count == 0) return;
                RichDocument.Copy(Composer.Document);
                ShowCopied(CopyComposerButton);
                feedbackTimer.Stop();
                feedbackTimer.Start();
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show(this, "无法复制内容：" + error.Message, "复制", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ------------------------------------------------------------ finishing a card

        /// <summary>Cards on their way to 已完成; pressing Enter again on one does nothing.</summary>
        private readonly HashSet<Idea> completing = new HashSet<Idea>();

        /// <summary>Windows' "show animations" setting; the self-test always animates.</summary>
        private static bool Motion => UiTestMode || SystemParameters.ClientAreaAnimation;

        private void CompleteIdea(Idea idea)
        {
            if (idea == null || idea.Done || completing.Contains(idea)) return;
            // A question is finished by answering it, not by a tick.
            if (idea.IsQuestion)
            {
                OpenAnswerSheet(idea);
                return;
            }
            FinishCard(idea);
        }

        private void FinishCard(Idea idea)
        {
            if (idea == null || idea.Done || completing.Contains(idea)) return;
            var container = OpenList.ItemContainerGenerator.ContainerFromItem(idea) as ListBoxItem;
            var button = container == null ? null : FindNamed<Button>(container, "CardComplete");
            if (button == null || !Motion)
            {
                Move(idea, true);
                return;
            }
            completing.Add(idea);
            AnimateCheck(button, () => SendToDone(idea, container));
        }

        /// <summary>
        /// After the tick: the card lifts out and the cards below close the gap under
        /// it; then it is filed, and its picture arcs over to its row in 已完成,
        /// shrinking to the row's height. The row fades in as the picture lands and
        /// the 已完成 count gives one small bump. About 0.8 s in all, and the record
        /// is saved before the flight starts, so nothing waits on the animation.
        /// </summary>
        private void SendToDone(Idea idea, ListBoxItem container)
        {
            if (container == null || !container.IsVisible || !IsAncestorOf(container) || FlyCanvas == null)
            {
                completing.Remove(idea);
                Move(idea, true);
                return;
            }

            var window = container.TransformToAncestor(this).TransformBounds(new Rect(container.RenderSize));
            var viewport = OpenList.TransformToAncestor(this).TransformBounds(new Rect(OpenList.RenderSize));
            var visible = Rect.Intersect(window, viewport);
            if (!visible.IsEmpty && visible.Height > 12)
                window = visible;
            var offset = TransformToVisual(FlyCanvas).Transform(new System.Windows.Point(0, 0));
            var from = new Rect(window.Left + offset.X, window.Top + offset.Y, window.Width, window.Height);

            var ghost = new Border
            {
                Width = from.Width,
                Height = from.Height,
                CornerRadius = new CornerRadius(12),
                Background = CardSnapshot(window)
            };
            var scale = new ScaleTransform(1, 1);
            var move = new TranslateTransform(from.Left, from.Top);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(move);
            ghost.RenderTransform = group;
            Canvas.SetLeft(ghost, 0);
            Canvas.SetTop(ghost, 0);
            FlyCanvas.Children.Add(ghost);

            // lift: the picture rises a little while the slot under it closes
            var soft = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from.Top, from.Top - 5, TimeSpan.FromMilliseconds(160)) { EasingFunction = soft });
            var height = container.ActualHeight;
            var margin = container.Margin;
            container.Opacity = 0;
            container.Height = height;
            var close = new CubicEase { EasingMode = EasingMode.EaseInOut };
            container.BeginAnimation(HeightProperty,
                new DoubleAnimation(height, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = close, BeginTime = TimeSpan.FromMilliseconds(40) });
            container.BeginAnimation(MarginProperty,
                new ThicknessAnimation(margin, new Thickness(margin.Left, 0, margin.Right, 0), TimeSpan.FromMilliseconds(200))
                { EasingFunction = close, BeginTime = TimeSpan.FromMilliseconds(40) });

            RunLater(240, () =>
            {
                completing.Remove(idea);
                Move(idea, true);

                // the container is recycled for other cards: give it back untouched
                container.BeginAnimation(HeightProperty, null);
                container.BeginAnimation(MarginProperty, null);
                container.ClearValue(HeightProperty);
                container.ClearValue(MarginProperty);
                container.Opacity = 1;

                // 已完成 is never scrolled to chase the record. If its row happens to be
                // on screen the picture lands on it; otherwise it simply sinks into the
                // top of the column and fades, and the count bump says where it went.
                DoneList.UpdateLayout();
                var row = DoneList.ItemContainerGenerator.ContainerFromItem(idea) as ListBoxItem;
                FrameworkElement aim = DoneList.IsVisible ? DoneList : DoneCountPill;
                var box = aim.TransformToVisual(FlyCanvas).TransformBounds(new Rect(aim.RenderSize));
                var to = new Rect(box.Left + 8, box.Top + 6, Math.Max(40, box.Width - 16), 30);
                if (row != null && row.IsVisible && row.ActualHeight > 0)
                {
                    var slot = row.TransformToVisual(FlyCanvas).TransformBounds(new Rect(row.RenderSize));
                    if (slot.Top >= box.Top - 1 && slot.Bottom <= box.Bottom + 1)
                    {
                        to = slot;
                        row.Opacity = 0;
                    }
                    else
                        row = null;
                }
                else
                    row = null;
                FlyToDone(ghost, scale, move, to, row);
            });
        }

        private void FlyToDone(Border ghost, ScaleTransform scale, TranslateTransform move, Rect to, ListBoxItem row)
        {
            var s = Math.Clamp(to.Height / Math.Max(1, ghost.Height), 0.35, 1.0);
            var endX = to.Left;
            var endY = to.Top + (to.Height - ghost.Height * s) / 2;
            var startY = move.Y;
            var flight = TimeSpan.FromMilliseconds(380);

            move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(move.X, endX, flight) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

            // a shallow arc: up first, then down into the row
            var arc = new DoubleAnimationUsingKeyFrames { Duration = flight };
            arc.KeyFrames.Add(new EasingDoubleKeyFrame(startY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            arc.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Min(startY, endY) - 22, KeyTime.FromPercent(0.38),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            arc.KeyFrames.Add(new EasingDoubleKeyFrame(endY, KeyTime.FromPercent(1),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            move.BeginAnimation(TranslateTransform.YProperty, arc);

            var shrink = new DoubleAnimation(1, s, flight) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
            ghost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(260) });

            if (row != null)
            {
                var slide = new TranslateTransform(-10, 0);
                row.RenderTransform = slide;
                var land = new CubicEase { EasingMode = EasingMode.EaseOut };
                row.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(270), EasingFunction = land });
                slide.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(270), EasingFunction = land });
            }

            RunLater(360, BumpDoneCount);
            RunLater(560, () =>
            {
                FlyCanvas.Children.Remove(ghost);
                if (row != null)
                {
                    row.BeginAnimation(OpacityProperty, null);
                    row.Opacity = 1;
                    row.RenderTransform = null;
                }
            });
        }

        /// <summary>The 已完成 count swells once and flashes green as the card arrives.</summary>
        private void BumpDoneCount()
        {
            if (DoneCountPill == null) return;
            DoneCountPill.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            var bump = new ScaleTransform(1, 1);
            DoneCountPill.RenderTransform = bump;
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromPercent(0.4), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            bump.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            bump.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

            var green = ((SolidColorBrush)FindResource("DoneGreen")).Color;
            var flash = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, green.R, green.G, green.B));
            DoneCountPill.Background = flash;
            flash.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(System.Windows.Media.Color.FromArgb(0, green.R, green.G, green.B), TimeSpan.FromMilliseconds(520))
                { BeginTime = TimeSpan.FromMilliseconds(120) });
        }

        /// <summary>Back to 未完成 (Enter on a finished row): it slides in where it lands.</summary>
        private void RestoreIdea(Idea idea)
        {
            if (idea == null) return;
            Move(idea, false);
            if (!Motion) return;
            OpenList.UpdateLayout();
            if (OpenList.ItemContainerGenerator.ContainerFromItem(idea) is not ListBoxItem card) return;
            var slide = new TranslateTransform(0, -8);
            card.RenderTransform = slide;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            RunLater(260, () =>
            {
                card.BeginAnimation(OpacityProperty, null);
                card.Opacity = 1;
                if (ReferenceEquals(card.RenderTransform, slide)) card.RenderTransform = null;
            });
        }

        // ------------------------------------------------------------ questions

        /// <summary>The question the answer sheet is open for.</summary>
        private Idea answering;
        /// <summary>Opened on a finished question: the sheet only edits the answer.</summary>
        private bool answeringDone;
        /// <summary>What was typed before an Esc, so a second look starts where you left.</summary>
        private readonly Dictionary<Idea, string> answerDrafts = new Dictionary<Idea, string>();

        private bool AnswerOpen => AnswerLayer != null && AnswerLayer.Visibility == Visibility.Visible;

        private void ToggleQuestion(Idea idea)
        {
            if (idea == null) return;
            idea.IsQuestion = !idea.IsQuestion;
            store.Save();
        }

        private void OpenAnswerSheet(Idea idea)
        {
            if (idea == null) return;
            AbortPendingGesture();
            answering = idea;
            answeringDone = idea.Done;

            var question = (idea.Text ?? "").Trim();
            AnswerQuestion.Text = question.Length > 0 ? question : idea.Preview;
            var meta = "记于 " + idea.TimeLabel;
            if (idea.HasAnswer && DateTimeStamp.TryParse(idea.AnsweredTime, out var answered))
                meta += " · 回答于 " + answered.ToString(answered.Year == DateTime.Now.Year ? "MM-dd HH:mm" : "yyyy-MM-dd HH:mm");
            AnswerMeta.Text = meta;
            AnswerBox.Text = answerDrafts.TryGetValue(idea, out var draft) ? draft : idea.Answer;
            AnswerBox.CaretIndex = AnswerBox.Text.Length;
            AnswerDone.Content = answeringDone ? "保存回答" : "回答并完成";
            AnswerSave.Visibility = answeringDone ? Visibility.Collapsed : Visibility.Visible;
            AnswerHint.Text = answeringDone
                ? "Shift+Enter 换行"
                : "Shift+Enter 换行";
            AnswerPlaceholder.Text = "写回答…";
            AnswerPlaceholder.Foreground = (Brush)FindResource("Faint");
            UpdateAnswerState();

            AnswerLayer.Visibility = Visibility.Visible;
            if (Motion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                AnswerLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
                var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
                AnswerScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                AnswerScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
                AnswerShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
            else
            {
                AnswerLayer.BeginAnimation(OpacityProperty, null);
                AnswerLayer.Opacity = 1;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                AnswerBox.Focus();
                Keyboard.Focus(AnswerBox);
            }));
        }

        private void CloseAnswerSheet(bool keepDraft, Action after)
        {
            if (!AnswerOpen)
            {
                after?.Invoke();
                return;
            }
            var idea = answering;
            if (idea != null)
            {
                if (keepDraft && (AnswerBox.Text ?? "") != idea.Answer && (AnswerBox.Text ?? "").Trim().Length > 0)
                    answerDrafts[idea] = AnswerBox.Text;
                else
                    answerDrafts.Remove(idea);
            }
            answering = null;
            if (!Motion)
            {
                AnswerLayer.Visibility = Visibility.Collapsed;
                after?.Invoke();
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
            fade.Completed += (s, e) =>
            {
                if (answering != null) return; // opened again meanwhile
                AnswerLayer.Visibility = Visibility.Collapsed;
                AnswerLayer.BeginAnimation(OpacityProperty, null);
                AnswerLayer.Opacity = 1;
                after?.Invoke();
            };
            var shrink = new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
            AnswerScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            AnswerScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
            AnswerLayer.BeginAnimation(OpacityProperty, fade);
        }

        private void FocusAfterAnswer()
        {
            if (OpenList.SelectedItem is Idea && OpenList.ItemContainerGenerator.ContainerFromItem(OpenList.SelectedItem) is ListBoxItem item)
                item.Focus();
            else
                OpenList.Focus();
        }

        /// <summary>Enter / 回答并完成: keep the answer, then finish with the usual tick and flight.</summary>
        private void SubmitAnswer(bool complete)
        {
            var idea = answering;
            if (idea == null) return;
            var text = (AnswerBox.Text ?? "").Trim();
            if (text.Length == 0)
            {
                NudgeEmptyAnswer();
                return;
            }
            if (idea.Answer != text)
            {
                idea.Answer = text;
                idea.AnsweredTime = DateTimeStamp.Now();
            }
            idea.IsQuestion = true;
            answerDrafts.Remove(idea);
            store.Save();
            var finish = complete && !idea.Done;
            CloseAnswerSheet(false, () =>
            {
                if (finish)
                {
                    OpenList.SelectedItem = idea;
                    FinishCard(idea);
                }
                else
                    FocusAfterAnswer();
            });
        }

        /// <summary>An empty answer cannot finish a question: the field shakes once and says why.</summary>
        private void NudgeEmptyAnswer()
        {
            AnswerPlaceholder.Text = "写了回答才能完成";
            AnswerPlaceholder.Foreground = (Brush)FindResource("QuestionInk");
            if (!Motion) return;
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            double[] xs = { 0, -7, 6, -4, 3, -1, 0 };
            for (var i = 0; i < xs.Length; i++)
                shake.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromPercent(i / (double)(xs.Length - 1))));
            AnswerNudge.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        private void UpdateAnswerState()
        {
            var has = (AnswerBox.Text ?? "").Trim().Length > 0;
            AnswerPlaceholder.Visibility = (AnswerBox.Text ?? "").Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            AnswerDone.IsEnabled = has;
            AnswerSave.IsEnabled = has;
        }

        private void AnswerBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AnswerPlaceholder == null || AnswerDone == null) return;
            if (AnswerPlaceholder.Foreground != FindResource("Faint") && (AnswerBox.Text ?? "").Length > 0)
            {
                AnswerPlaceholder.Text = "写回答…";
                AnswerPlaceholder.Foreground = (Brush)FindResource("Faint");
            }
            UpdateAnswerState();
        }

        private void AnswerBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (AnswerFrame == null) return;
            var focused = AnswerBox.IsKeyboardFocusWithin;
            AnswerFrame.BorderBrush = (Brush)FindResource(focused ? "QuestionRing" : "Line");
            AnswerFrame.Background = focused ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xFA));
        }

        private void AnswerDone_Click(object sender, RoutedEventArgs e) => SubmitAnswer(!answeringDone);

        private void AnswerSave_Click(object sender, RoutedEventArgs e) => SubmitAnswer(false);

        /// <summary>A click on the dimmed list closes the sheet; what was typed is kept as a draft.</summary>
        private void AnswerLayer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseAnswerSheet(true, FocusAfterAnswer);
            e.Handled = true;
        }

        private void AnswerCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void CardQuestion_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ToggleQuestion(IdeaOf(sender));
        }

        private void CardQuestionMenu_Click(object sender, RoutedEventArgs e)
        {
            ToggleQuestion(IdeaOf(sender));
        }

        private void CardAnswerMenu_Click(object sender, RoutedEventArgs e)
        {
            OpenAnswerSheet(IdeaOf(sender));
        }

        private void RunLater(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        private void CompleteButton_Click(object sender, RoutedEventArgs e)
        {
            CompleteIdea(OpenList.SelectedItem as Idea);
        }

        private void CardComplete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            CompleteIdea((sender as Button)?.DataContext as Idea);
        }

        private static T FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var element = child as T;
                if (element != null && element.Name == name)
                    return element;
                var found = FindNamed<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void AnimateCheck(Button button, Action finished)
        {
            // The tick belongs to the record itself: it draws inside the card's own
            // circle, then the card travels to the finished column.
            var glyph = FindNamed<FrameworkElement>(button, "checkGlyph");
            var ring = FindNamed<System.Windows.Shapes.Ellipse>(button, "ring");
            var tick = FindNamed<System.Windows.Shapes.Path>(button, "tick");
            if (glyph == null || ring == null || tick == null)
            {
                finished();
                return;
            }

            var fill = new SolidColorBrush(Colors.White);
            var edge = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0xAE, 0xC3));
            ring.Fill = fill;
            ring.Stroke = edge;
            tick.Visibility = Visibility.Visible;
            tick.StrokeDashOffset = TickDash;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var accent = ((SolidColorBrush)FindResource("Accent")).Color;
            var from = edge.Color;
            var to = ((SolidColorBrush)FindResource("AccentDeep")).Color;
            fill.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Colors.White, accent, new Duration(TimeSpan.FromMilliseconds(160)))
                { EasingFunction = ease });
            edge.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(160))) { EasingFunction = ease });
            tick.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(TickDash, 0, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = TimeSpan.FromMilliseconds(50),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });

            var pop = new DoubleAnimationUsingKeyFrames();
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)),
                new CubicEase { EasingMode = EasingMode.EaseInOut }));
            var scale = glyph.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1, 1);
                glyph.RenderTransform = scale;
            }
            glyph.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

            // Hold the finished tick for a beat before the card travels across.
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(265) };
            delay.Tick += (s, e) =>
            {
                delay.Stop();
                finished();
            };
            delay.Start();
        }

        private static Idea IdeaOf(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as Idea;
        }

        private Idea FocusedIdea()
        {
            if (OpenList.IsKeyboardFocusWithin)
                return OpenList.SelectedItem as Idea;
            if (DoneList.IsKeyboardFocusWithin)
                return DoneList.SelectedItem as Idea;
            return null;
        }

        private void EditIdea(Idea idea)
        {
            if (idea == null)
                return;
            if (ReferenceEquals(editingIdea, idea))
            {
                Composer.Focus();
                return;
            }
            if (!idea.Done)
            {
                BeginComposerEdit(idea);
                return;
            }
            var editor = new EditWindow(idea.Text, idea.RichContent, textScale) { Owner = this };
            if (editor.ShowDialog() != true)
                return;
            idea.UpdateContent(editor.EditedText, editor.EditedRich);
            store.Save();
            Refresh();
        }

        private void BeginComposerEdit(Idea idea)
        {
            if (!ResolveComposerEdit())
                return;

            SaveDraft();
            parkedDraft = new ParkedDraft(Composer.Document, Composer.Selection.Start,
                Composer.Selection.End, Composer.VerticalOffset);
            var document = RichDocument.CreateEditorDocument(Composer, idea.Text, idea.RichContent);
            editingIdea = idea;
            Composer.Document = document;
            Composer.CaretPosition = document.ContentEnd;
            composerEditChanged = false;
            UpdateComposerMode();
            Composer.Focus();
        }

        private bool SaveComposerEdit()
        {
            if (composerEditChanged)
            {
                var text = RichDocument.Text(Composer.Document).Trim();
                var richContent = RichDocument.Serialize(Composer.Document);
                if (text.Length == 0 && richContent == null)
                {
                    Composer.Focus();
                    return false;
                }
                editingIdea.UpdateContent(text, richContent);
                store.Save();
                Refresh();
            }
            EndComposerEdit();
            return true;
        }

        private bool ResolveComposerEdit()
        {
            if (editingIdea == null)
                return true;
            if (composerEditChanged)
            {
                var result = System.Windows.MessageBox.Show(this, "修改还没保存，要保存吗？",
                    "编辑事项", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                    return SaveComposerEdit();
                if (result != MessageBoxResult.No)
                    return false;
            }
            EndComposerEdit();
            return true;
        }

        private void EndComposerEdit()
        {
            Composer.Document = parkedDraft.Document;
            Composer.Selection.Select(parkedDraft.SelectionStart, parkedDraft.SelectionEnd);
            Composer.ScrollToVerticalOffset(parkedDraft.ScrollOffset);
            parkedDraft = null;
            editingIdea = null;
            composerEditChanged = false;
            UpdateComposerMode();
            Composer.Focus();
        }

        private void UpdateComposerMode()
        {
            var editing = editingIdea != null;
            ComposerTitle.Text = editing ? "编辑事项" : "随手记";
            ComposerShortcuts.Text = editing ? "Enter 保存 · Shift+Enter 换行 · Esc 取消"
                : "Enter 记录 · Shift+Enter 换行";
            AddButton.Content = editing ? "保存修改" : "记录";
            AddButton.ToolTip = editing ? "保存 · Enter" : "记录 · Enter";
            CancelComposerEditButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            UpdateComposerPlaceholder();
            UpdateComposerHint();
        }

        private void EditOpenButton_Click(object sender, RoutedEventArgs e)
        {
            EditIdea(OpenList.SelectedItem as Idea);
        }

        private void CancelComposerEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (editingIdea != null)
                EndComposerEdit();
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            DeleteIdea((sender as Button)?.CommandParameter as Idea);
        }

        private void DeleteIdea(Idea idea)
        {
            if (idea == null)
                return;
            var answer = System.Windows.MessageBox.Show(this,
                "删除后无法恢复。\n\n" + idea.Preview, "删除事项",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;

            var list = idea.Done ? DoneList : OpenList;
            var selectedIndex = ReferenceEquals(list.SelectedItem, idea) ? list.SelectedIndex : -1;
            if (!store.Ideas.Remove(idea))
                return;
            if (ReferenceEquals(editingIdea, idea))
                EndComposerEdit();
            store.Save();
            Refresh();
            if (selectedIndex >= 0 && list.Items.Count > 0)
                list.SelectedIndex = Math.Min(selectedIndex, list.Items.Count - 1);
        }

        private void CardEditMenu_Click(object sender, RoutedEventArgs e)
        {
            EditIdea(IdeaOf(sender));
        }

        private void CardCopyMenu_Click(object sender, RoutedEventArgs e)
        {
            Copy(IdeaOf(sender), null);
        }

        private void CardStarMenu_Click(object sender, RoutedEventArgs e)
        {
            var idea = IdeaOf(sender);
            if (idea == null)
                return;
            idea.Starred = !idea.Starred;
            store.Save();
            Refresh();
        }

        private void CardShelveMenu_Click(object sender, RoutedEventArgs e)
        {
            ToggleShelve(IdeaOf(sender));
        }

        private void CardToggleMenu_Click(object sender, RoutedEventArgs e)
        {
            var idea = IdeaOf(sender);
            if (idea == null)
                return;
            if (idea.Done)
                Move(idea, false);
            else
                CompleteIdea(idea);
        }

        private void CardDeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            DeleteIdea(IdeaOf(sender));
        }

        private void CardStar_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button button || button.DataContext is not Idea idea)
                return;
            idea.Starred = !idea.Starred;
            store.Save();
            Refresh();
        }

        private void StarFilter_Toggled(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                Refresh();
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            RestoreIdea(DoneList.SelectedItem as Idea);
        }

        private void CopyOpenButton_Click(object sender, RoutedEventArgs e)
        {
            Copy(OpenList.SelectedItem as Idea, CopyOpenButton);
        }

        private void CopyDoneButton_Click(object sender, RoutedEventArgs e)
        {
            Copy(DoneList.SelectedItem as Idea, CopyDoneButton);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded)
                return;
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                Refresh();
                return;
            }
            SearchHint.Visibility = Visibility.Collapsed;
            searchTimer.Stop();
            searchTimer.Start();
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateActionState();
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var list = sender as ListBox;
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source != list)
            {
                // A double click on the circle or the star is theirs, not the card's.
                if (source is System.Windows.Controls.Primitives.ButtonBase) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            var idea = list == null ? null : list.SelectedItem as Idea;
            if (idea == null)
                return;

            // The second click of a double click also starts a hold; drop it before
            // the dialog swallows the mouse-up and leaves the tray on screen.
            AbortPendingGesture();

            // Unfinished records go straight to the composer.
            if (!idea.Done)
            {
                EditIdea(idea);
                return;
            }

            if (idea.IsQuestion || idea.HasAnswer)
            {
                OpenAnswerSheet(idea);
                return;
            }
            var detail = new DetailWindow(idea.Text, idea.RichContent, textScale) { Owner = this };
            detail.ShowDialog();
        }

        private void PinButton_Toggled(object sender, RoutedEventArgs e)
        {
            Topmost = PinButton.IsChecked == true;
            if (IsLoaded)
                PersistRatio();
        }

        private bool dividerDragging;
        private double dividerGrabOffset;
        private double dividerTarget = double.NaN;
        private bool dividerFramePending;

        /// <summary>
        /// Column resize. Two things made the old version lag and sometimes ignore
        /// the drag: Thumb.HorizontalChange is measured from the thumb's *current*
        /// position, which moves with the column, so adding it to the start point
        /// fought the layout every frame and snapped the divider back. Now the
        /// position comes straight from the mouse relative to the column host, and
        /// the width is written at most once per rendered frame. The card lists keep
        /// a frozen width during the drag so no card re-wraps until release.
        /// </summary>
        private void Divider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            var mouse = Mouse.GetPosition(ColumnHost).X;
            dividerGrabOffset = mouse - OpenColumn.ActualWidth;
            dividerDragging = true;
            OpenList.Width = OpenList.ActualWidth;
            DoneList.Width = DoneList.ActualWidth;
            OpenList.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            DoneList.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            OpenColumnRoot.ClipToBounds = true;
            DoneColumnRoot.ClipToBounds = true;
        }

        private void Divider_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (!dividerDragging)
                return;
            var min = Math.Max(OpenColumn.MinWidth, 120);
            var max = Math.Max(ColumnHost.ActualWidth - Math.Max(DoneColumn.MinWidth, 120) - ColumnDivider.ActualWidth, min + 1);
            dividerTarget = Math.Clamp(Mouse.GetPosition(ColumnHost).X - dividerGrabOffset, min, max);
            if (!dividerFramePending)
            {
                dividerFramePending = true;
                CompositionTarget.Rendering += ApplyDividerFrame;
            }
        }

        private void ApplyDividerFrame(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= ApplyDividerFrame;
            dividerFramePending = false;
            if (!dividerDragging || double.IsNaN(dividerTarget))
                return;
            if (Math.Abs(OpenColumn.ActualWidth - dividerTarget) >= 0.5)
                OpenColumn.Width = new GridLength(dividerTarget, GridUnitType.Pixel);
        }

        private void Divider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (dividerDragging)
            {
                dividerDragging = false;
                if (dividerFramePending)
                {
                    CompositionTarget.Rendering -= ApplyDividerFrame;
                    dividerFramePending = false;
                }
                var open = double.IsNaN(dividerTarget) ? OpenColumn.ActualWidth : dividerTarget;
                dividerTarget = double.NaN;

                OpenList.Width = double.NaN;
                DoneList.Width = double.NaN;
                OpenList.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                DoneList.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                OpenColumnRoot.ClipToBounds = false;
                DoneColumnRoot.ClipToBounds = false;

                var host = Math.Max(1, ColumnHost.ActualWidth - ColumnDivider.ActualWidth);
                var ratio = Math.Clamp(open / host, 0.15, 0.85);
                OpenColumn.Width = new GridLength(ratio, GridUnitType.Star);
                DoneColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
            }
            PersistRatio();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // The answer sheet owns the keyboard while it is up.
            if (AnswerOpen)
            {
                if (e.Key == Key.Escape)
                {
                    CloseAnswerSheet(true, FocusAfterAnswer);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    SubmitAnswer(!answeringDone);
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Escape && (draggingCard || pressArmed))
            {
                CancelDrag();
                CancelLongPress();
                e.Handled = true;
                return;
            }

            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && (e.Key == Key.OemPlus || e.Key == Key.Add ||
                            e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                // Same ladder as Ctrl+wheel, for people who never touch the wheel.
                StepTextScale(e.Key == Key.OemPlus || e.Key == Key.Add ? 1 : -1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (Composer.IsKeyboardFocusWithin)
                {
                    if (SubmitComposer())
                        Hide();
                    e.Handled = true;
                }
                else if (!(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
                {
                    SubmitComposer();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Enter && Composer.IsKeyboardFocusWithin &&
                     (Keyboard.Modifiers & ModifierKeys.Shift) == 0 &&
                     (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                // Enter submits the composer, Shift+Enter inserts a line break.
                SubmitComposer();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                     !Composer.IsKeyboardFocusWithin)
            {
                // Enter on a focused card does what its circle does; keyboard-first
                // completion without reaching for the mouse.
                if (OpenList.IsKeyboardFocusWithin && OpenList.SelectedItem is Idea open)
                {
                    CompleteIdea(open);
                    e.Handled = true;
                }
                else if (DoneList.IsKeyboardFocusWithin && DoneList.SelectedItem is Idea done)
                {
                    RestoreIdea(done);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.P &&
                     (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                     (ModifierKeys.Control | ModifierKeys.Shift))
            {
                // Park / un-park the selected card without touching its state.
                ToggleShelve(FocusedIdea());
                e.Handled = true;
            }
            else if (e.Key == Key.S &&
                     (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                     (ModifierKeys.Control | ModifierKeys.Shift))
            {
                StarFilter.IsChecked = StarFilter.IsChecked != true;
                e.Handled = true;
            }
            else if (e.Key == Key.Q &&
                     (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                     (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ToggleQuestion(FocusedIdea());
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                var idea = FocusedIdea();
                if (idea != null)
                {
                    EditIdea(idea);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete && !Composer.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin)
            {
                var idea = FocusedIdea();
                if (idea != null)
                {
                    DeleteIdea(idea);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (editingIdea != null && Composer.IsKeyboardFocusWithin)
                {
                    EndComposerEdit();
                    e.Handled = true;
                    return;
                }
                if (openWeek != null || doneWeek != null)
                {
                    CloseDrill();
                    e.Handled = true;
                    return;
                }
                if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
                {
                    SearchBox.Text = "";
                    e.Handled = true;
                    return;
                }
                // The composer is deliberately left alone: the draft is auto-saved,
                // and wiping a long thought on a stray Esc was a real data loss.
                if (currentLabel != null)
                {
                    currentLabel = null;
                    Refresh();
                    e.Handled = true;
                    return;
                }
                if (currentTag != null)
                {
                    SetTag(null);
                    e.Handled = true;
                    return;
                }
                if (ShelveFilter.IsChecked == true)
                {
                    ShelveFilter.IsChecked = false;
                    e.Handled = true;
                    return;
                }
                if (StarFilter.IsChecked == true)
                {
                    StarFilter.IsChecked = false;
                    e.Handled = true;
                    return;
                }
                Hide();
                e.Handled = true;
            }
        }
    }
}


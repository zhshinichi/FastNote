using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace FastNote
{
    public static class Program
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        // Versions before 1.0 were called Scratchpad and used these names. They are
        // still checked so a copy from before the rename can be asked to close.
        private const string LegacyMutex = @"Local\ScratchpadWpf.SingleInstance";
        private const string LegacyExit = @"Local\ScratchpadWpf.Exit";

        /// <summary>
        /// FastNote.exe [--startup] [--exit] [--data-dir=DIR] [--instance=NAME]
        ///   --startup    start in the tray only (for the Startup folder)
        ///   --exit       ask the running copy to close (draft and layout are saved);
        ///                exit code 0 = closed or not running, 2 = still running after 15 s
        ///   --data-dir   use another data folder; --instance lets that copy run beside
        ///                the normal one
        /// Tests: --selftest, --uitest, --uitest-drag=DIR, --uitest-capture=FILE.png
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            var dataDirectory = Option(args, "--data-dir=");
            var instance = Option(args, "--instance=");
            var startHidden = args.Contains("--startup");
            var uiTest = args.Contains("--uitest");
            if (!string.IsNullOrEmpty(dataDirectory))
                AppPaths.CrashLog = Path.Combine(dataDirectory, "crash.log");

            if (args.Contains("--selftest"))
            {
                // Writes test records, so it never runs on the real data folder.
                if (string.IsNullOrEmpty(dataDirectory))
                {
                    Environment.ExitCode = 2;
                    return;
                }
                StoreSelfTest(dataDirectory);
                return;
            }
            if (uiTest && (string.IsNullOrEmpty(dataDirectory) || string.IsNullOrEmpty(instance)))
                throw new ArgumentException("--uitest needs both --data-dir and --instance.");

            AppDomain.CurrentDomain.UnhandledException += (s, e) => AppPaths.Log("FATAL", e.ExceptionObject);
            try
            {
                var suffix = string.IsNullOrEmpty(instance) ? "" : "." + instance;
                var mutexName = @"Local\FastNote.SingleInstance" + suffix;
                var exitSignalName = @"Local\FastNote.Exit" + suffix;

                if (args.Contains("--exit"))
                {
                    var asked = Signal(exitSignalName) | Signal(LegacyExit + suffix);
                    Environment.ExitCode = !asked || WaitUntilGone(15, mutexName, LegacyMutex + suffix) ? 0 : 2;
                    return;
                }

                using (var mutex = new Mutex(true, mutexName, out var createdNew))
                {
                    if (!createdNew)
                        return;

                    // A pre-rename copy still running would keep writing to the old folder.
                    if (Signal(LegacyExit + suffix))
                        WaitUntilGone(15, LegacyMutex + suffix);
                    if (string.IsNullOrEmpty(dataDirectory))
                        AppPaths.MigrateLegacyData();
                    if (uiTest && !File.Exists(Path.Combine(dataDirectory, "ideas.tsv")))
                        TestData.Seed(dataDirectory);

                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    UiFonts.Install(app);
                    var window = new MainWindow(dataDirectory);
                    app.MainWindow = window;

                    // Listener for --exit: one named event per instance, so a test copy
                    // and the normal one never close each other.
                    var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, exitSignalName);
                    var exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal,
                        (state, timedOut) => app.Dispatcher.BeginInvoke(new Action(window.RequestExit)),
                        null, -1, false);
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        AppPaths.Log("UI", e.Exception);
                        e.Handled = false;
                    };
                    if (uiTest)
                    {
                        // Off screen, never activated, no taskbar button: a test run
                        // does not get in the way of whoever is using the machine.
                        MainWindow.UiTestMode = true;
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = -4000; window.Top = -4000;
                        window.ShowActivated = false; window.ShowInTaskbar = false;
                        var handle = new WindowInteropHelper(window).EnsureHandle();
                        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
                    }
                    if (startHidden)
                    {
                        // The hotkey hook needs a window handle even while hidden.
                        new WindowInteropHelper(window).EnsureHandle();
                    }
                    else
                    {
                        window.Show();
                        if (uiTest)
                            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                                new Action(() => RunUiTests(window, args, dataDirectory)));
                    }
                    app.Run();
                    exitWait.Unregister(null);
                    exitSignal.Dispose();
                }
            }
            catch (Exception error)
            {
                AppPaths.Log("MAIN", error);
                throw;
            }
        }

        private static string Option(string[] args, string prefix)
        {
            var argument = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
            return argument?.Substring(prefix.Length);
        }

        private static bool Signal(string name)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(name))
                    signal.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }

        private static bool WaitUntilGone(int seconds, params string[] mutexNames)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                var running = false;
                foreach (var name in mutexNames)
                {
                    try
                    {
                        using (Mutex.OpenExisting(name)) { }
                        running = true;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                    }
                }
                if (!running)
                    return true;
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(200);
            }
        }

        private static void RunUiTests(MainWindow window, string[] args, string dataDirectory)
        {
            window.UpdateLayout();
            var open = (System.Windows.Controls.ColumnDefinition)window.FindName("OpenColumn");
            var done = (System.Windows.Controls.ColumnDefinition)window.FindName("DoneColumn");
            File.WriteAllText(Path.Combine(dataDirectory, "uitest-layout.txt"),
                $"open={open.ActualWidth} done={done.ActualWidth} ratio={open.ActualWidth / (open.ActualWidth + done.ActualWidth)}");
            var dragFolder = Option(args, "--uitest-drag=");
            if (dragFolder != null)
                window.RunDragSelfTest(dragFolder);
            var capture = Option(args, "--uitest-capture=");
            if (capture != null)
            {
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture)));
                using var file = File.Create(capture);
                encoder.Save(file);
            }
        }

        /// <summary>
        /// Round trip through ideas.tsv and layout.ini: write, reload, compare. The
        /// result line goes to selftest.txt in the data folder.
        /// </summary>
        private static void StoreSelfTest(string dataDirectory)
        {
            var store = new Store(dataDirectory);
            store.Load();
            var before = store.Ideas.Count;
            var idea = new Idea
            {
                Time = DateTimeStamp.Now(), Done = false,
                Text = "自检 %0A 与\t制表符\n第二行",
                Starred = true, Shelved = true,
                Tag = "自检标签", Label = "自检摘要",
                IsQuestion = true,
                // commas, tabs, line breaks and the flag words themselves must survive
                Answer = "回答, star shelved question\t制表\n第二行 %0A",
                AnsweredTime = DateTimeStamp.Now()
            };
            // One second apart: records are matched by their time stamp.
            var plain = new Idea
            {
                Time = DateTime.Now.AddSeconds(-1).ToString("yyyyMMddHHmmss"),
                Done = false, Text = "自检普通行"
            };
            store.Ideas.Insert(0, plain);
            store.Ideas.Insert(0, idea);
            store.Save();
            Store.Flush();
            var reloaded = new Store(dataDirectory);
            reloaded.Load();
            var back = reloaded.Ideas.FirstOrDefault(i => i.Time == idea.Time);
            var backPlain = reloaded.Ideas.FirstOrDefault(i => i.Time == plain.Time);
            store.SaveSettings(0.42, false, 168, 1.3);
            Store.Flush();
            var layoutProbe = new Store(dataDirectory);
            var scaleOk = Math.Abs(layoutProbe.LoadTextScale() - 1.3) < 0.001;
            var ratioOk = Math.Abs(layoutProbe.LoadSplitRatio() - 0.42) < 0.001;
            var hotkeyOk = layoutProbe.LoadHotkey() == Hotkey.Default &&
                           Hotkey.TryParse("ctrl + alt + space", out var parsed) && parsed.Text == "Ctrl+Alt+Space" &&
                           !Hotkey.TryParse("Q", out _) && Hotkey.Parse("nonsense").Text == Hotkey.Default;
            var result = "before=" + before + " after=" + reloaded.Ideas.Count +
                         " same=" + (back != null && back.Text == idea.Text) +
                         " flags=" + (back != null && back.Starred && back.Shelved) +
                         " question=" + (back != null && back.IsQuestion && back.Answer == idea.Answer &&
                                         back.AnsweredTime == idea.AnsweredTime) +
                         " plain=" + (backPlain != null && backPlain.Text == "自检普通行" &&
                                      !backPlain.HasTag && !backPlain.HasLabel && !backPlain.IsQuestion) +
                         " tag=" + (back != null && back.Tag == "自检标签") +
                         " label=" + (back != null && back.Label == "自检摘要") +
                         " scale=" + scaleOk + " ratio=" + ratioOk + " hotkey=" + hotkeyOk +
                         " text=[" + (back == null ? "null" : back.Text) + "]";
            File.WriteAllText(Path.Combine(dataDirectory, "selftest.txt"), result);
        }
    }
}

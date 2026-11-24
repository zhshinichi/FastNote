using System.IO;
using System.Text;
using System.Threading;

namespace FastNote
{
    /// <summary>
    /// The data folder: ideas.tsv (one item per line), tags.txt, layout.ini and
    /// the composer draft. See README.md for the column layout.
    /// </summary>
    public class Store
    {
        public string Directory { get; private set; }
        public string IdeasFile { get { return Path.Combine(Directory, "ideas.tsv"); } }
        public string DraftFile { get { return Path.Combine(Directory, "todo-draft.txt"); } }
        public string LayoutFile { get { return Path.Combine(Directory, "layout.ini"); } }
        public string TagsFile { get { return Path.Combine(Directory, "tags.txt"); } }

        public List<Idea> Ideas { get; private set; } = new List<Idea>();

        // One queue for the whole process: every Store instance writes full-file
        // snapshots, so the last snapshot queued for a path is the one that matters.
        private static readonly object WriteGate = new object();
        private static readonly Dictionary<string, Action<TextWriter>> PendingWrites =
            new(System.StringComparer.OrdinalIgnoreCase);
        private static Thread writeThread;
        private static bool writeBusy;

        public Store(string directory = null)
        {
            Directory = directory ?? AppPaths.DefaultDataDirectory;
            System.IO.Directory.CreateDirectory(Directory);
        }

        public void Load()
        {
            Ideas = new List<Idea>();
            if (!File.Exists(IdeasFile))
                return;
            foreach (var line in File.ReadLines(IdeasFile, Encoding.UTF8))
            {
                var fields = line.Split('\t');
                // 3 columns = plain record, 4 = with rich content, 5 = flag list,
                // 6 = tag, 7 = label. Anything else is a foreign line, not ours.
                if (fields.Length < 3 || fields.Length > 7 || fields[0].Length != 14)
                    continue;
                var rich = fields.Length >= 4 && fields[3].Length > 0 ? fields[3] : null;
                // Column five is a small flag list: "star", "shelved" or both.
                var flags = fields.Length >= 5 ? fields[4] : string.Empty;
                var tag = fields.Length >= 6 ? Decode(fields[5]) : string.Empty;
                var label = fields.Length == 7 ? Decode(fields[6]) : string.Empty;
                ReadFlags(flags, out var star, out var shelved, out var question, out var answer, out var answeredAt);
                Ideas.Add(new Idea
                {
                    Time = fields[0],
                    Done = fields[1] == "1",
                    Text = Decode(fields[2]),
                    RichContent = rich,
                    Starred = star,
                    Shelved = shelved,
                    Tag = tag,
                    Label = label,
                    IsQuestion = question,
                    Answer = answer,
                    AnsweredTime = answeredAt
                });
            }
        }

        /// <summary>
        /// Column five is a comma list: "star", "shelved", "question", "a:&lt;hex&gt;" (the
        /// answer as UTF-8 hex, so it can never hold a tab, a comma, or the words older
        /// builds look for) and "at:&lt;stamp&gt;". Older builds still read star/shelved
        /// from it and skip the rest, so no record is ever dropped by them.
        /// </summary>
        private static void ReadFlags(string flags, out bool star, out bool shelved, out bool question,
            out string answer, out string answeredAt)
        {
            star = shelved = question = false;
            answer = "";
            answeredAt = "";
            foreach (var raw in (flags ?? "").Split(','))
            {
                var token = raw.Trim();
                if (token.StartsWith("a:", StringComparison.Ordinal))
                {
                    try
                    {
                        answer = Encoding.UTF8.GetString(Convert.FromHexString(token.Substring(2)));
                    }
                    catch (FormatException)
                    {
                        // a damaged answer is dropped, the record itself is kept
                    }
                    continue;
                }
                if (token.StartsWith("at:", StringComparison.Ordinal))
                {
                    answeredAt = token.Substring(3);
                    continue;
                }
                if (token == "question")
                {
                    question = true;
                    continue;
                }
                if (token.IndexOf("star", StringComparison.Ordinal) >= 0) star = true;
                if (token.IndexOf("shelved", StringComparison.Ordinal) >= 0) shelved = true;
            }
        }

        /// <summary>
        /// Tags exist on their own, not only as a property of records: an empty tag
        /// is a real thing you are about to fill, so it needs its own list.
        /// </summary>
        public List<string> LoadTags()
        {
            var tags = new List<string>();
            if (!File.Exists(TagsFile))
                return tags;
            foreach (var line in File.ReadAllLines(TagsFile, Encoding.UTF8))
            {
                var name = line.Trim();
                if (name.Length > 0 && !tags.Contains(name))
                    tags.Add(name);
            }
            return tags;
        }

        public void SaveTags(IEnumerable<string> tags)
        {
            var names = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            WriteAtomic(TagsFile, names.Count == 0 ? "" : string.Join("\n", names) + "\n");
        }

        public void Save()
        {
            // Capture values on the UI thread. The writer never reads mutable Ideas.
            var snapshot = new StoredIdea[Ideas.Count];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var idea = Ideas[i];
                snapshot[i] = new StoredIdea(idea.Time, idea.Done, idea.Text, idea.RichContent,
                    idea.Starred, idea.Shelved, idea.Tag, idea.Label,
                    idea.IsQuestion, idea.Answer, idea.AnsweredTime);
            }
            WriteAtomic(IdeasFile, writer => WriteIdeas(writer, snapshot));
        }

        private readonly record struct StoredIdea(string Time, bool Done, string Text, string RichContent,
            bool Starred, bool Shelved, string Tag, string Label,
            bool Question, string Answer, string AnsweredAt);

        private static void WriteIdeas(TextWriter writer, StoredIdea[] ideas)
        {
            foreach (var idea in ideas)
            {
                // Columns: time, done, text, rich, flags, tag, label. Trailing empty
                // ones are dropped, but a later column forces the earlier ones to
                // exist - building the list explicitly keeps that honest.
                var fields = new List<string>
                {
                    idea.Time,
                    idea.Done ? "1" : "0",
                    Encode(idea.Text)
                };

                var rich = idea.RichContent ?? "";
                var flagList = new List<string>();
                if (idea.Starred) flagList.Add("star");
                if (idea.Shelved) flagList.Add("shelved");
                if (idea.Question) flagList.Add("question");
                if (!string.IsNullOrEmpty(idea.Answer))
                    flagList.Add("a:" + Convert.ToHexString(Encoding.UTF8.GetBytes(idea.Answer)));
                if (!string.IsNullOrEmpty(idea.AnsweredAt) && idea.AnsweredAt.Length == 14)
                    flagList.Add("at:" + idea.AnsweredAt);
                var flags = string.Join(",", flagList);
                var tag = idea.Tag ?? "";
                var label = idea.Label ?? "";

                var last = 2;
                if (rich.Length > 0) last = 3;
                if (flags.Length > 0) last = 4;
                if (tag.Length > 0) last = 5;
                if (label.Length > 0) last = 6;

                while (fields.Count <= last)
                    fields.Add("");

                // Only the columns up to "last" exist - writing past the end is what
                // crashed plain three-column records before.
                if (last >= 3) fields[3] = rich;
                if (last >= 4) fields[4] = flags;
                if (last >= 5) fields[5] = Encode(tag);
                if (last >= 6) fields[6] = Encode(label);

                writer.Write(fields[0]);
                for (var column = 1; column < fields.Count; column++)
                {
                    writer.Write('\t');
                    writer.Write(fields[column]);
                }
                writer.Write('\n');
            }
        }

        private string lastDraftText;
        private string lastDraftRich;

        public string LoadDraft()
        {
            lastDraftText = File.Exists(DraftFile) ? File.ReadAllText(DraftFile, Encoding.UTF8) : "";
            return lastDraftText;
        }

        public string LoadRichDraft()
        {
            var path = Path.Combine(Directory, "todo-draft.rich");
            lastDraftRich = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            return lastDraftRich.Length == 0 ? null : lastDraftRich;
        }

        public void SaveDraft(string text, string richContent = null)
        {
            richContent = richContent ?? "";
            // Image drafts serialize to megabytes; skip the write when nothing moved.
            if (text == lastDraftText && richContent == lastDraftRich)
                return;
            lastDraftText = text;
            lastDraftRich = richContent;
            // An empty rich file also invalidates an earlier image draft.
            WriteAtomic(Path.Combine(Directory, "todo-draft.rich"), richContent);
            WriteAtomic(DraftFile, text);
        }

        public double LoadComposerHeight()
        {
            return double.TryParse(ReadSetting("ComposerHeight"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var height)
                && double.IsFinite(height) ? Math.Clamp(height, 120, 800) : 160;
        }

        public double LoadSplitRatio()
        {
            var ratio = 0.5;
            var saved = ReadSetting("SplitRatio");
            if (!string.IsNullOrEmpty(saved))
            {
                double parsed;
                if (double.TryParse(saved, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    ratio = Math.Max(0.15, Math.Min(0.85, parsed));
            }
            return ratio;
        }

        public bool LoadPinned()
        {
            return ReadSetting("Pinned") == "1";
        }

        /// <summary>How much the record text is enlarged (1 = the original 14pt).</summary>
        public double LoadTextScale()
        {
            if (double.TryParse(ReadSetting("TextScale"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var scale) &&
                double.IsFinite(scale))
                return Math.Clamp(scale, 1.0, 1.5);
            return 1.0;
        }

        public void SaveSettings(double splitRatio, bool pinned, double composerHeight, double textScale = 1.0)
        {
            // Keep keys this version does not write itself (Hotkey, anything newer).
            var keys = new List<string>();
            if (File.Exists(LayoutFile))
            {
                foreach (var line in File.ReadAllLines(LayoutFile, Encoding.UTF8))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("["))
                        continue;
                    var name = text.Split('=')[0].Trim();
                    if (name.Equals("SplitRatio", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Pinned", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ComposerHeight", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("TextScale", StringComparison.OrdinalIgnoreCase))
                        continue;
                    keys.Add(text);
                }
            }
            // Written once so the setting is easy to find and change by hand.
            if (!keys.Any(k => k.Split('=')[0].Trim().Equals("Hotkey", StringComparison.OrdinalIgnoreCase)))
                keys.Add("Hotkey=" + Hotkey.Default);
            keys.Insert(0, "TextScale=" + textScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            keys.Insert(0, "ComposerHeight=" + composerHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            keys.Insert(0, "SplitRatio=" + splitRatio.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            keys.Insert(0, "Pinned=" + (pinned ? "1" : "0"));
            WriteAtomic(LayoutFile, "[Window]\n" + string.Join("\n", keys) + "\n");
        }

        /// <summary>"Hotkey=Ctrl+Alt+Q" in layout.ini; null when it is not set.</summary>
        public string LoadHotkey()
        {
            return ReadSetting("Hotkey");
        }

        private string ReadSetting(string name)
        {
            if (!File.Exists(LayoutFile))
                return null;
            foreach (var line in File.ReadAllLines(LayoutFile, Encoding.UTF8))
            {
                var text = line.Trim();
                var index = text.IndexOf('=');
                if (index <= 0)
                    continue;
                if (text.Substring(0, index).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return text.Substring(index + 1).Trim();
            }
            return null;
        }

        private static string Encode(string text)
        {
            return (text ?? "").Replace("%", "%25").Replace("\t", "%09")
                               .Replace("\r", "%0D").Replace("\n", "%0A");
        }

        private static string Decode(string text)
        {
            return text.Replace("%0A", "\n").Replace("%0D", "\r")
                       .Replace("%09", "\t").Replace("%25", "%");
        }

        /// <summary>
        /// Writes are queued, never performed on the calling thread. ideas.tsv is
        /// ~1.8MB once a note carries pasted rich text, and rewriting it inline costs
        /// ~35ms (8ms write + 27ms ReplaceFile) - paid on every add, tag, star and
        /// completion. One background thread owns the disk instead, and because each
        /// queued item is a whole-file snapshot the newest one simply wins.
        /// </summary>
        private static void WriteAtomic(string path, string content) =>
            WriteAtomic(path, writer => writer.Write(content));

        private static void WriteAtomic(string path, Action<TextWriter> write)
        {
            lock (WriteGate)
            {
                PendingWrites[path] = write;
                if (writeThread == null)
                {
                    writeThread = new Thread(WriteLoop)
                    {
                        IsBackground = true,
                        Name = "fastnote-store"
                    };
                    writeThread.Start();
                }
                Monitor.Pulse(WriteGate);
            }
        }

        /// <summary>Blocks until everything queued has reached the disk.</summary>
        public static void Flush()
        {
            lock (WriteGate)
            {
                while (PendingWrites.Count > 0 || writeBusy)
                    Monitor.Wait(WriteGate, 5000);
            }
        }

        private static void WriteLoop()
        {
            while (true)
            {
                string path;
                Action<TextWriter> write;
                lock (WriteGate)
                {
                    while (PendingWrites.Count == 0)
                        Monitor.Wait(WriteGate);
                    var next = PendingWrites.First();
                    path = next.Key;
                    write = next.Value;
                    PendingWrites.Remove(path);
                    writeBusy = true;
                }
                try
                {
                    WriteAtomicNow(path, write);
                }
                catch (System.Exception error)
                {
                    // A failed write must not kill the app; the next snapshot retries.
                    AppPaths.Log("STORE", error);
                }
                finally
                {
                    lock (WriteGate)
                    {
                        writeBusy = false;
                        Monitor.PulseAll(WriteGate);
                    }
                }
            }
        }

        private static void WriteAtomicNow(string path, Action<TextWriter> write)
        {
            var temporary = path + ".wpf.tmp";
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                write(writer);
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
    }
}

using System;
using System.IO;

namespace FastNote
{
    /// <summary>Where FastNote keeps its data and its crash log.</summary>
    public static class AppPaths
    {
        private static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string DefaultDataDirectory => Path.Combine(LocalAppData, "FastNote");

        /// <summary>Versions before 1.0 were called Scratchpad and kept their data here.</summary>
        public static string LegacyDataDirectory => Path.Combine(LocalAppData, "Scratchpad");

        /// <summary>Lives in the data folder; a test run with --data-dir logs into its own folder.</summary>
        public static string CrashLog { get; set; } = Path.Combine(DefaultDataDirectory, "crash.log");

        public static void Log(string kind, object error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
                File.AppendAllText(CrashLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + kind + " " + error + Environment.NewLine);
            }
            catch (Exception)
            {
                // nowhere left to report to
            }
        }

        /// <summary>
        /// First start after the rename: copy the old Scratchpad folder into the new
        /// one. The old folder is left untouched. ideas.tsv is copied last, so a copy
        /// that fails half way is simply tried again on the next start.
        /// </summary>
        public static void MigrateLegacyData()
        {
            try
            {
                var source = LegacyDataDirectory;
                var target = DefaultDataDirectory;
                if (File.Exists(Path.Combine(target, "ideas.tsv")) || !File.Exists(Path.Combine(source, "ideas.tsv")))
                    return;
                CopyFolder(source, target, skipIdeas: true);
                File.Copy(Path.Combine(source, "ideas.tsv"), Path.Combine(target, "ideas.tsv"));
            }
            catch (Exception error)
            {
                Log("MIGRATE", error);
            }
        }

        private static void CopyFolder(string source, string target, bool skipIdeas)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                if ((skipIdeas && name.Equals("ideas.tsv", StringComparison.OrdinalIgnoreCase)) ||
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("crash.log", StringComparison.OrdinalIgnoreCase))
                    continue;
                var to = Path.Combine(target, name);
                if (!File.Exists(to))
                    File.Copy(file, to);
            }
            foreach (var folder in Directory.GetDirectories(source))
                CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)), false);
        }
    }
}

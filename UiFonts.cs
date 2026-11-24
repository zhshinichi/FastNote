using System;
using System.IO;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace FastNote
{
    /// <summary>
    /// The UI typeface. HarmonyOS Sans SC ships in Fonts\ next to the exe, so it
    /// works without installing anything; WPF reads it straight from that folder.
    /// If the folder is missing the app falls back to Noto Sans SC / YaHei UI.
    /// Each weight is a real static face (Regular, Medium), so no fake bolding.
    /// </summary>
    public static class UiFonts
    {
        public static FontFamily Ui { get; private set; } =
            new FontFamily("Noto Sans SC, Microsoft YaHei UI, Segoe UI");

        public static FontFamily Medium { get; private set; } =
            new FontFamily("Noto Sans SC Medium, Noto Sans SC, Microsoft YaHei UI");

        public static void Install(Application app)
        {
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "Fonts");
                if (File.Exists(Path.Combine(folder, "HarmonyOS_Sans_SC_Regular.ttf")))
                {
                    var baseUri = new Uri(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                    Ui = new FontFamily(baseUri, "./#HarmonyOS Sans SC, Noto Sans SC, Microsoft YaHei UI");
                    // The Medium file names itself both "HarmonyOS Sans SC Medium" (legacy
                    // family) and "HarmonyOS Sans SC" + Medium (typographic family); list
                    // both so WPF finds it whichever name it groups by.
                    Medium = new FontFamily(baseUri,
                        "./#HarmonyOS Sans SC Medium, ./#HarmonyOS Sans SC, Noto Sans SC Medium, Microsoft YaHei UI");
                }
            }
            catch (Exception)
            {
                // Keep the system fallback.
            }
            app.Resources["UiFont"] = Ui;
            app.Resources["UiFontMedium"] = Medium;
        }
    }
}

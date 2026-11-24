using System;
using System.ComponentModel;
using System.Globalization;

namespace FastNote
{
    /// <summary>Week boundaries shared by grouping, headers and the compact rows.</summary>
    public static class Weeks
    {
        public static DateTime Start(DateTime value)
        {
            // Weeks run Monday..Sunday, matching how the records are usually read.
            var date = value.Date;
            return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }

        public static string Range(DateTime start)
        {
            return start.ToString("MM/dd", CultureInfo.InvariantCulture) + " – " +
                   start.AddDays(6).ToString("MM/dd", CultureInfo.InvariantCulture);
        }

        public static string Tag(DateTime start)
        {
            var thisWeek = Start(DateTime.Now);
            if (start == thisWeek) return "本周";
            if (start == thisWeek.AddDays(-7)) return "上周";
            return string.Empty;
        }
    }

    /// <summary>
    /// One week's group key: carries the header text. Records point at the same
    /// instance, so grouping by it is exact.
    /// </summary>
    public sealed class WeekSummary : INotifyPropertyChanged
    {
        private int total;

        public WeekSummary(DateTime start, int total, int preview)
        {
            Start = start;
            this.total = total;
            Preview = preview;
        }

        public DateTime Start { get; }
        public int Total
        {
            get => total;
            internal set
            {
                if (total == value)
                    return;
                total = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMore)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FooterText)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public int Preview { get; }
        public string Label { get { return Weeks.Range(Start); } }
        public string Tag { get { return Weeks.Tag(Start); } }
        public string CountText { get { return Total + " 条"; } }
        public bool HasMore { get { return Total > Preview; } }
        public string FooterText { get { return "查看全部 " + Total + " 条 →"; } }
    }
}

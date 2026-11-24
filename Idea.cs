using System.ComponentModel;
using System.Text.RegularExpressions;

namespace FastNote
{
    /// <summary>One item: a line of ideas.tsv.</summary>
    public class Idea : INotifyPropertyChanged
    {
        private bool starred;
        private bool shelved;

        public string Time { get; set; }
        public bool Done { get; set; }

        // Starred = worth keeping in sight (reused entries, half-finished threads).
        public bool Starred
        {
            get { return starred; }
            set
            {
                if (starred == value) return;
                starred = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Starred)));
            }
        }

        // Shelved = still to do, just not now. Kept out of the open column so it
        // cannot dilute the things that actually need doing today.
        public bool Shelved
        {
            get { return shelved; }
            set
            {
                if (shelved == value) return;
                shelved = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shelved)));
            }
        }

        // Tag = the little bucket a record lives in ("#RL论文"). One record, one
        // tag: picking a tag is a place to stand, not a label salad.
        public string Tag
        {
            get { return tag; }
            set
            {
                var next = value ?? "";
                if (tag == next) return;
                tag = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tag)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTag)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagLabel)));
            }
        }

        private string tag = "";

        public bool HasTag { get { return tag.Length > 0; } }
        public string TagLabel { get { return "#" + tag; } }

        // Label = the one-line gist of what is inside ("消融实验", "reviewer 2").
        // Only for sorting and searching; it never changes how a record behaves.
        public string Label
        {
            get { return label; }
            set
            {
                var next = value ?? "";
                if (label == next) return;
                label = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLabel)));
            }
        }

        private string label = "";

        public bool HasLabel { get { return label.Length > 0; } }

        // Question = something you once asked yourself. It is not finished by
        // ticking it off but by writing down the answer, which stays with it.
        public bool IsQuestion
        {
            get { return question; }
            set
            {
                if (question == value) return;
                question = value;
                Notify(nameof(IsQuestion));
                Notify(nameof(CompleteHint));
            }
        }

        private bool question;

        public string Answer
        {
            get { return answer; }
            set
            {
                var next = value ?? "";
                if (answer == next) return;
                answer = next;
                Notify(nameof(Answer));
                Notify(nameof(HasAnswer));
                Notify(nameof(AnswerPreview));
            }
        }

        private string answer = "";

        public bool HasAnswer { get { return answer.Trim().Length > 0; } }
        public string AnswerPreview { get { return Regex.Replace(answer, @"\s+", " ").Trim(); } }

        /// yyyyMMddHHmmss of the last time the answer was written.
        public string AnsweredTime { get; set; } = "";

        public string CompleteHint { get { return question ? "回答并完成" : "标记完成"; } }

        /// New records ending in a question mark start out as questions.
        public static bool LooksLikeQuestion(string text)
        {
            var trimmed = (text ?? "").TrimEnd();
            return trimmed.EndsWith("?") || trimmed.EndsWith("？");
        }

        private void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public string Text { get; set; }
        // Optional WPF XamlPackage, present only for records containing images.
        public string RichContent { get; set; }

        public void UpdateContent(string text, string richContent)
        {
            Text = text;
            RichContent = richContent;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }

        private DateTime? parsedDate;

        public DateTime Date
        {
            get
            {
                if (parsedDate == null)
                {
                    DateTimeStamp.TryParse(Time, out var stamp);
                    parsedDate = stamp;
                }
                return parsedDate.Value;
            }
        }

        /// Monday 00:00 of the record's week.
        public DateTime WeekStart { get { return Weeks.Start(Date); } }

        /// Set by the grouped view before binding.
        public WeekSummary Week { get; set; }

        /// Short stamp for the compact (grouped) rows: the week header explains the rest.
        public string ShortTime
        {
            get
            {
                var date = Date;
                var now = DateTime.Now;
                if (date.Date == now.Date) return date.ToString("HH:mm");
                if (date.Date == now.Date.AddDays(-1)) return "昨天";
                if (date.Year == now.Year) return date.ToString("MM-dd");
                return date.ToString("yyyy-MM-dd");
            }
        }

        public string Preview
        {
            get { var text = Regex.Replace(Text ?? "", @"\s+", " "); return string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(RichContent) ? "[图片]" : text + (!string.IsNullOrEmpty(RichContent) ? "  [图片]" : ""); }
        }

        public string Meta
        {
            // The column already says open/finished, so the card only carries time.
            get { return TimeLabel; }
        }

        public string TimeLabel
        {
            get
            {
                DateTimeStamp.TryParse(Time, out var stamp);
                var local = stamp;
                var now = DateTime.Now;
                if (local.Date == now.Date)
                    return "今天 " + local.ToString("HH:mm");
                if (local.Date == now.Date.AddDays(-1))
                    return "昨天 " + local.ToString("HH:mm");
                if (local.Year == now.Year)
                    return local.ToString("MM-dd HH:mm");
                return local.ToString("yyyy-MM-dd HH:mm");
            }
        }
    }

    /// <summary>Parses the yyyyMMddHHmmss stamp used by the store.</summary>
    public static class DateTimeStamp
    {
        public static bool TryParse(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrEmpty(value) || value.Length != 14)
                return false;
            return DateTime.TryParseExact(value, "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.None, out result);
        }

        public static string Now()
        {
            return DateTime.Now.ToString("yyyyMMddHHmmss");
        }
    }
}

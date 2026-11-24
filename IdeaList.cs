using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FastNote
{
    internal sealed class IdeaList : ObservableCollection<Idea>
    {
        public void Update(List<Idea> ideas)
        {
            if (this.SequenceEqual(ideas))
                return;

            var previous = new HashSet<Idea>(this);
            var next = new HashSet<Idea>(ideas);
            var added = ideas.Count(idea => !previous.Contains(idea));
            var removed = this.Count(idea => !next.Contains(idea));

            // A few changes keep the existing containers and scroll position.
            // A large scope change needs only one notification to the view.
            if (added + removed > 24)
            {
                CheckReentrancy();
                Items.Clear();
                foreach (var idea in ideas)
                    Items.Add(idea);
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                return;
            }

            for (var index = Count - 1; index >= 0; index--)
            {
                if (!next.Contains(this[index]))
                    RemoveAt(index);
            }
            for (var index = 0; index < ideas.Count; index++)
            {
                var idea = ideas[index];
                if (index < Count && ReferenceEquals(this[index], idea))
                    continue;
                if (previous.Contains(idea))
                    Move(IndexOf(idea), index);
                else
                    Insert(index, idea);
            }
        }
    }
}

using System.Collections.ObjectModel;

namespace Harness.Ui.ViewModels;

/// <summary>Updating small bound lists in place, so a live reload keeps the controls of rows that did not change.</summary>
public static class Lists
{
    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="wanted"/> (same order) touching only what differs:
    /// equal items stay, changed ones are replaced in their place, others are moved, added or removed.
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> wanted)
    {
        ArgumentNullException.ThrowIfNull(target);
        var items = wanted.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], items[i]))
            {
                continue;
            }
            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (EqualityComparer<T>.Default.Equals(target[j], items[i]))
                {
                    found = j;
                    break;
                }
            }
            if (found >= 0)
            {
                target.Move(found, i);
            }
            else if (i < target.Count)
            {
                target[i] = items[i];
            }
            else
            {
                target.Add(items[i]);
            }
        }
        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

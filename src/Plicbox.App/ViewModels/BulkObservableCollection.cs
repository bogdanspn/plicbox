using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Plicbox.App.ViewModels;

/// <summary>
/// ObservableCollection that can swap its whole content with one Reset notification. Adding hundreds of items one
/// by one makes a grouped WPF view regroup after every item, which is what made switching folders feel slow.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Adds items at the end with one Reset (WPF views don't take multi-item adds).</summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

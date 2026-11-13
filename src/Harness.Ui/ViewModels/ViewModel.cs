using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Harness.Ui.ViewModels;

/// <summary>A view model's change notification; compiled bindings listen to it.</summary>
public abstract class ViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and notifies; false when the value did not change.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Notify(name);
        return true;
    }

    protected void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

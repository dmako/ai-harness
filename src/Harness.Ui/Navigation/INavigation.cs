namespace Harness.Ui.Navigation;

/// <summary>
/// The page's history: the browser host keeps it in the address bar (history.pushState, and Back/Forward come back
/// as hash changes); the desktop host and tests keep it in memory. The shell shows <see cref="Current"/>.
/// </summary>
public interface INavigation
{
    Route Current { get; }

    /// <summary>Raised when the page changes, by <see cref="Go"/>, <see cref="Back"/> or the browser's own buttons.</summary>
    event EventHandler? Changed;

    /// <summary>Goes to <paramref name="href"/> (a hash address), adding it to the history.</summary>
    void Go(string href);

    /// <summary>Shows <paramref name="href"/> in place of the current page (a moved address, a chosen default).</summary>
    void Replace(string href);

    void Back();

    void Forward();
}

/// <summary>A history kept in memory: the desktop host's, and the tests'.</summary>
public sealed class InMemoryNavigation : INavigation
{
    private readonly List<string> _entries = [];
    private int _at = -1;

    public InMemoryNavigation(string start = "#/")
    {
        Push(start);
    }

    public Route Current { get; private set; } = Route.Home;

    public event EventHandler? Changed;

    public void Go(string href)
    {
        _entries.RemoveRange(_at + 1, _entries.Count - _at - 1);
        Push(href);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Replace(string href)
    {
        var route = Route.Parse(href);
        _entries[_at] = route.Href;
        Current = route;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Back() => Move(-1);

    public void Forward() => Move(1);

    private void Push(string href)
    {
        var route = Route.Parse(href);
        _entries.Add(route.Href);
        _at = _entries.Count - 1;
        Current = route;
    }

    private void Move(int by)
    {
        var to = _at + by;
        if (to < 0 || to >= _entries.Count)
        {
            return;
        }
        _at = to;
        Current = Route.Parse(_entries[_at]);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

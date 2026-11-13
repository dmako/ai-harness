namespace Harness.Ui.Services;

/// <summary>
/// What this page keeps for the person using it (app.js store): in the browser its localStorage under "harness.", so
/// both UIs on one address share them; in memory on the desktop and in tests. Never shared between browsers.
/// </summary>
public interface IPreferences
{
    string? Get(string key);

    void Set(string key, string value);
}

/// <summary>Preferences kept for as long as the page lives.</summary>
public sealed class InMemoryPreferences : IPreferences
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;
}

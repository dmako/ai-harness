using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A source location and its loaded snippet or an explanation when it cannot be read.</summary>
public sealed partial class EvidencePane(string location) : ObservableObject
{
    public string Location { get; } = location;

    [ObservableProperty]
    public partial string Text { get; set; } = "Loading…";
}

/// <summary>Source snippets for one location or a comparison of related locations.</summary>
public sealed class EvidenceDialog : DialogViewModel<bool>
{
    public ObservableCollection<EvidencePane> Panes { get; } = [];

    protected override Task AcceptAsync()
    {
        Close(true);
        return Task.CompletedTask;
    }

    public async Task LoadAsync(UiServices services, string endpoint, IReadOnlyList<string> locations, int span)
    {
        foreach (var location in locations)
        {
            Panes.Add(new EvidencePane(location));
        }
        await Task.WhenAll(Panes.Select(p => LoadPaneAsync(services, endpoint, p, span)));
    }

    private static async Task LoadPaneAsync(UiServices services, string endpoint, EvidencePane pane, int span)
    {
        try
        {
            var path = $"{endpoint}?at={Uri.EscapeDataString(pane.Location)}&span={span.ToString(CultureInfo.InvariantCulture)}";
            var answer = await services.Api.ReadAsync(path, CancellationToken.None);
            var snippet = answer.GetProperty("snippet");
            var start = snippet.TryGetProperty("start", out var n) ? n.GetInt32() : 0;
            var folder = snippet.TryGetProperty("folder", out var f) && f.ValueKind is JsonValueKind.True;
            var focus = snippet.TryGetProperty("focus", out var range) && range.ValueKind is JsonValueKind.Array
                ? range.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.Number).Select(x => x.GetInt32()).ToArray()
                : [];
            var lines = snippet.TryGetProperty("lines", out var ls) && ls.ValueKind is JsonValueKind.Array
                ? ls.EnumerateArray().Select((line, i) => FormatLine(line, start + i, folder, focus))
                : [];
            pane.Text = string.Join("\n", lines);
            if (pane.Text.Length is 0)
            {
                pane.Text = "(No source lines available.)";
            }
            if (snippet.TryGetProperty("truncated", out var cut) && cut.ValueKind is JsonValueKind.True)
            {
                pane.Text += "\n… truncated";
            }
        }
        catch (Exception e) when (e is Harness.Ui.Api.HarnessApiException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            pane.Text = "Source unavailable: " + e.Message;
        }
    }

    private static string? FormatLine(JsonElement line, int number, bool folder, int[] focus)
    {
        if (folder)
        {
            return line.GetString();
        }
        var marker = focus.Length >= 2 && number >= focus[0] && number <= focus[1] ? '>' : ' ';
        return $"{marker}{number,5}  {line.GetString()}";
    }
}

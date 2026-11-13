using System.Runtime.CompilerServices;
using System.Text;

namespace Harness.Ui.Api;

/// <summary>A minimal text/event-stream reader: events with their name ("message" without one) and joined data.</summary>
internal static class ServerSentEvents
{
    public static async IAsyncEnumerable<(string Name, string Data)> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken token
    )
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var name = "";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length is 0)
            {
                if (data.Length > 0)
                {
                    yield return (name.Length is 0 ? "message" : name, data.ToString());
                }
                name = "";
                data.Clear();
                continue;
            }
            if (line.StartsWith(':', StringComparison.Ordinal))
            {
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    name = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }
                    data.Append(value);
                    break;
                default:
                    break;
            }
        }
    }
}

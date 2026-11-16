using System.Net;

using System.Text;

namespace Harness.Ui.Preview;

/// <summary>Answers the designer from bundled fixtures without a network connection or state writes.</summary>
internal sealed class PreviewApiHandler : HttpMessageHandler
{
    private const string Prefix = "PreviewFixtures/";
    private readonly Dictionary<string, string> _answers = ReadFixtures();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Method != HttpMethod.Get)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }

        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "projects")
        {
            segments[2] = "{id}";
            if (segments.Length >= 5 && segments[3] == "tasks")
            {
                segments[4] = "{task}";
            }
        }
        else if (segments.Length >= 4 && segments[0] == "api" && segments[1] == "workspaces")
        {
            segments[2] = "{name}";
        }
        else if (segments.Length == 3 && segments[0] == "api" && segments[1] == "docs")
        {
            segments[2] = "{slug}";
        }

        var key = string.Join('/', segments) + ".json";
        return Task.FromResult(_answers.TryGetValue(key, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Dictionary<string, string> ReadFixtures()
    {
        var assembly = typeof(PreviewApiHandler).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result[name[Prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }
        return result;
    }
}

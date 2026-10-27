using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture;

public sealed partial class SystemReader
{
    [GeneratedRegex(@"(?i)(?:^|;)\s*(?:database|initial catalog)\s*=\s*([^;]+)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex Database { get; }

    [GeneratedRegex(
        "^\\s*(?:export\\s+)?((?:VITE|REACT_APP|NEXT_PUBLIC|NUXT_PUBLIC|PUBLIC)_[A-Z0-9_]*(?:URL|HOST|ENDPOINT|ADDRESS|API)[A-Z0-9_]*)\\s*=\\s*['\"]?([^'\"#\\s]+)",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex EnvironmentUrl { get; }

    [GeneratedRegex(
        "\\.(?:subscribe|publish|Subscribe|Publish|SubscribeAsync|PublishAsync)\\(\\s*['\"]([^'\"]+)['\"]",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex Topic { get; }

    private static readonly (string Match, string Broker)[] Brokers =
    [
        ("masstransit", "message bus"),
        ("rabbitmq", "RabbitMQ"),
        ("amqp", "RabbitMQ"),
        ("kafka", "Kafka"),
        ("confluent", "Kafka"),
        ("azure.messaging.servicebus", "Azure Service Bus"),
        ("servicebus", "Azure Service Bus"),
        ("mqttnet", "MQTT"),
        ("mqtt", "MQTT"),
        ("nats", "NATS"),
        ("amazon.sqs", "SQS"),
        ("sqs", "SQS"),
    ];

    private void ReadConfiguration(SystemMember member, string file)
    {
        var name = Path.GetFileName(file);
        if (
            name.StartsWith("appsettings", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
        )
        {
            AppSettings(member, file);
        }
        else if (name.StartsWith(".env", StringComparison.Ordinal))
        {
            Environment(member, file);
        }
        else if (EcosystemReader.Of(file).Length > 0 && !EcosystemReader.Vendored(file))
        {
            Messaging(member, file);
        }
        else if (
            Path.GetExtension(file) is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs"
            && !file.Contains("node_modules/", StringComparison.Ordinal)
        )
        {
            Each(
                member,
                file,
                (line, text) =>
                {
                    foreach (var match in Topic.Matches(text).Select(match => match.Groups))
                    {
                        foreach (
                            var link in _links.Values.Where(link =>
                                link.From == member.Id
                                && link.Style is "messaging"
                                && !link.What.Contains("topic " + match[1].Value, StringComparer.Ordinal)
                            )
                        )
                        {
                            link.What.Add("topic " + match[1].Value);
                            if (link.Evidence.Count < 8)
                            {
                                link.Evidence.Add(Place(member.Id, At(file, line)));
                            }
                        }
                    }
                }
            );
        }
    }

    private void AppSettings(SystemMember member, string file)
    {
        var text = member.Repository.Read(file);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.TrimStart('\uFEFF'));
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            Walk(
                document.RootElement,
                [],
                (keys, value) =>
                {
                    var key = string.Join('.', keys);
                    var leaf = keys[^1];
                    string LineOf(string match)
                    {
                        var lines = AnalysisRepository.Lines(text);
                        var index = Array.FindIndex(
                            lines,
                            line =>
                                line.Contains('"' + leaf + '"', StringComparison.Ordinal)
                                && (match.Length is 0 || line.Contains(match, StringComparison.Ordinal))
                        );
                        return index < 0 ? file : At(file, index + 1);
                    }
                    if (keys.Length >= 2 && keys[0].Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
                    {
                        var database = First(Database, value).ToLowerInvariant();
                        if (database.Length is 0)
                        {
                            return;
                        }

                        if (!_databases.TryGetValue(database, out var places))
                        {
                            places = [];
                            _databases.Add(database, places);
                        }
                        if (!places.Any(place => place.Member == member.Id))
                        {
                            places.Add((member.Id, LineOf("")));
                        }

                        return;
                    }
                    if (
                        Plumbing.IsMatch(Norm(key))
                        || !UrlHost.IsMatch(value)
                        || !new[] { "url", "address", "endpoint", "host" }.Any(word =>
                            leaf.Contains(word, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        return;
                    }

                    var host = First(UrlHost, value);
                    if (host is "+" or "*" or "0.0.0.0")
                    {
                        return;
                    }

                    var section = keys[0].Split('[')[0];
                    var target = MatchMember(section);
                    var basis = target is null ? null : "name";
                    if (target is null)
                    {
                        target = MatchMember(host.Split('.')[0]);
                        basis = target is null ? null : "host";
                    }
                    if (target is null)
                    {
                        target = "ext:" + (Token(section).Length is 0 ? host : Token(section));
                        Node(new(target, Display(section), "external", Detail: key));
                    }
                    Link(
                        new(
                            member.Id,
                            target,
                            key.Contains("grpc", StringComparison.OrdinalIgnoreCase) ? "grpc" : "http"
                        )
                        {
                            What = [key],
                            Evidence = [Place(member.Id, LineOf(value[..Math.Min(value.Length, 40)]))],
                            Basis = basis,
                        }
                    );
                }
            );
        }
    }

    private void Environment(SystemMember member, string file) =>
        Each(
            member,
            file,
            (line, text) =>
            {
                var match = EnvironmentUrl.Match(text);
                if (!match.Success || Plumbing.IsMatch(Norm(match.Groups[1].Value)))
                {
                    return;
                }

                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value;
                var host = First(UrlHost, value);
                if (host.Length is 0)
                {
                    return;
                }

                var lower = (key + " " + value).ToLowerInvariant();
                if (
                    lower.Contains("mqtt", StringComparison.Ordinal)
                    || value.StartsWith("ws", StringComparison.Ordinal)
                        && lower.Contains("broker", StringComparison.Ordinal)
                )
                {
                    Node(new("broker:mqtt", "MQTT", "broker", Detail: key));
                    Link(
                        new(member.Id, "broker:mqtt", "messaging")
                        {
                            What = [key],
                            Evidence = [Place(member.Id, At(file, line))],
                        }
                    );
                    return;
                }
                var label = host.Split('.')[0];
                var target = MatchMember(label);
                var basis = target is null ? null : "host";
                if (target is null)
                {
                    target = "ext:" + Token(label);
                    Node(new(target, label, "external", Detail: key));
                }
                Link(
                    new(member.Id, target, "http")
                    {
                        What = [key],
                        Evidence = [Place(member.Id, At(file, line))],
                        Basis = basis,
                    }
                );
            }
        );

    private void Messaging(SystemMember member, string file)
    {
        foreach (var dependency in new EcosystemReader(member.Repository).Dependencies(file))
        {
            var name = dependency.Name.TrimStart('@').ToLowerInvariant();
            var broker = Brokers
                .FirstOrDefault(broker => name.StartsWith(broker.Match, StringComparison.Ordinal))
                .Broker;
            if (broker is null)
            {
                continue;
            }

            var id = "broker:" + Norm(broker);
            Node(new(id, broker, "broker"));
            Link(
                new(member.Id, id, "messaging")
                {
                    What = [(dependency.Name + " " + dependency.Version).Trim()],
                    Evidence = [Place(member.Id, At(dependency.Path, dependency.Line))],
                }
            );
        }
    }

    private static string Display(string text)
    {
        foreach (var suffix in new[] { "Grpc", "GRPC", "Url", "Client", "Api" })
        {
            if (text.Length > suffix.Length + 2 && text.EndsWith(suffix, StringComparison.Ordinal))
            {
                return text[..^suffix.Length];
            }
        }

        return text;
    }

    private static void Walk(JsonElement value, string[] keys, Action<string[], string> action)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    Walk(property.Value, [.. keys, property.Name], action);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Walk(
                        item,
                        keys.Length is 0
                            ? keys
                            :
                            [
                                .. keys[..^1],
                                keys[^1]
                                    + "["
                                    + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "]",
                            ],
                        action
                    );
                    index++;
                }
                break;
            case JsonValueKind.String when keys.Length > 0:
                action(keys, value.GetString() ?? "");
                break;
        }
    }
}

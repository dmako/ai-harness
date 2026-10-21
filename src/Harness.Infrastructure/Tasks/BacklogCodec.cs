using System.Globalization;
using System.Text;
using Harness.Core.Tasks;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Harness.Infrastructure.Tasks;

/// <summary>Strict version-one planning format. Execution fields are deliberately absent.</summary>
public static class BacklogCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static BacklogFile Read(string path) => Parse(Utf8.GetString(File.ReadAllBytes(path)), path);

    public static BacklogFile Parse(string text, string path = "backlog")
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            // Decode the first document, as Go's single yaml.Decoder.Decode does.
            var parser = new Parser(new StringReader(text.TrimStart('\uFEFF')));
            var stream = new YamlStream();
            // YamlStream validates the whole stream. Selecting the first document preserves
            // the backlog contract without deserializing arbitrary CLR types.
            stream.Load(new FirstDocumentParser(parser));
            if (stream.Documents.Count is 0)
                throw new InvalidDataException("unsupported backlog version 0 (want 1)");
            var root = Mapping(stream.Documents[0].RootNode, ["version", "milestones", "tasks"]);
            var versionText = Scalar(root.GetValueOrDefault("version"));
            var version = versionText.Length is 0 ? 0 : int.Parse(versionText, CultureInfo.InvariantCulture);
            if (version is not 1)
                throw new InvalidDataException($"unsupported backlog version {version} (want 1)");
            var tasks = Sequence(root.GetValueOrDefault("tasks")).Select(node =>
            {
                var d = Mapping(node, ["id", "milestone", "title", "type", "priority", "goal", "scope", "depends_on", "acceptance", "validation", "notes", "status"]);
                return new TaskDefinition
                {
                    Id = Text(d, "id"),
                    Milestone = Text(d, "milestone"),
                    Title = Text(d, "title"),
                    Type = Text(d, "type"),
                    Priority = Text(d, "priority"),
                    Goal = Text(d, "goal"),
                    Scope = Strings(d, "scope"),
                    DependsOn = Strings(d, "depends_on"),
                    Acceptance = Strings(d, "acceptance"),
                    Validation = Strings(d, "validation"),
                    Notes = Text(d, "notes"),
                    Status = Text(d, "status")
                };
            }).ToArray();
            var milestones = Sequence(root.GetValueOrDefault("milestones")).Select(node =>
            {
                var d = Mapping(node, ["id", "title", "goal", "exit", "notes"]);
                return new MilestoneDefinition(Text(d, "id"), Text(d, "title"), Text(d, "goal"), Strings(d, "exit"), Text(d, "notes"));
            }).ToArray();
            return new(version, tasks, milestones);
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException or FormatException or OverflowException)
        { throw new InvalidDataException($"{path}: {exception.Message}", exception); }
    }

    private static Dictionary<string, YamlNode> Mapping(YamlNode? node, string[] allowed)
        => Mapping(node, allowed, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));

    private static Dictionary<string, YamlNode> Mapping(YamlNode? node, string[] allowed, HashSet<YamlNode> ancestors)
    {
        if (node is not YamlMappingNode map)
            throw new InvalidDataException("expected a mapping");
        if (!ancestors.Add(map))
            throw new InvalidDataException("recursive YAML merge");
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach (var (key, value) in map.Children)
        {
            var name = Scalar(key);
            if (name is "<<" && key is YamlScalarNode { Style: ScalarStyle.Plain })
                continue;
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new InvalidDataException($"unknown field {TaskWorkflow.Quote(name)}");
            if (!result.TryAdd(name, value))
                throw new InvalidDataException($"field {TaskWorkflow.Quote(name)} is defined twice");
        }
        if (map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain }).Value is { } merge)
        {
            IEnumerable<YamlNode> maps = merge is YamlSequenceNode sequence ? sequence.Children : [merge];
            foreach (var merged in maps)
                foreach (var (key, value) in Mapping(merged, allowed, ancestors))
                    result.TryAdd(key, value);
        }
        ancestors.Remove(map);
        return result;
    }
    private static string Scalar(YamlNode? node) => node switch
    {
        null => "",
        YamlScalarNode { Value: null or "" } => "",
        YamlScalarNode scalar when scalar.Style is ScalarStyle.Plain && scalar.Value is "~" or "null" or "Null" or "NULL" => "",
        YamlScalarNode scalar => scalar.Value,
        _ => throw new InvalidDataException("expected a scalar string"),
    };
    private static IEnumerable<YamlNode> Sequence(YamlNode? node) => node switch
    {
        null => [],
        YamlScalarNode scalar when Scalar(scalar).Length is 0 => [],
        YamlSequenceNode sequence => sequence.Children,
        _ => throw new InvalidDataException("expected a sequence"),
    };
    private static string Text(Dictionary<string, YamlNode> d, string key) => Scalar(d.GetValueOrDefault(key));
    private static string[] Strings(Dictionary<string, YamlNode> d, string key) => Sequence(d.GetValueOrDefault(key)).Select(Scalar).ToArray();

    public static string Write(BacklogFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Dictionary<string, object> root = new() { ["version"] = file.Version };
        if (file.Milestones.Count is not 0)
            root["milestones"] = file.Milestones.Select(MilestoneMap).ToArray();
        root["tasks"] = file.Tasks.Select(DefinitionMap).ToArray();
        return Serialize(root);
    }
    internal static string WriteDefinitions(IReadOnlyList<TaskDefinition> definitions) => Serialize(definitions.Select(DefinitionMap).ToArray());
    private static string Serialize(object value) => new SerializerBuilder().WithIndentedSequences().ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve).Build().Serialize(value).Replace("\r\n", "\n", StringComparison.Ordinal);
    private static Dictionary<string, object> DefinitionMap(TaskDefinition d)
    {
        Dictionary<string, object> map = new() { ["id"] = d.Id };
        Add(map, "milestone", d.Milestone);
        map["title"] = d.Title;
        Add(map, "type", d.Type);
        Add(map, "priority", d.Priority);
        Add(map, "goal", d.Goal);
        Add(map, "scope", d.Scope);
        Add(map, "depends_on", d.DependsOn);
        Add(map, "acceptance", d.Acceptance);
        Add(map, "validation", d.Validation);
        Add(map, "notes", d.Notes);
        Add(map, "status", d.Status);
        return map;
    }
    private static Dictionary<string, object> MilestoneMap(MilestoneDefinition d)
    {
        Dictionary<string, object> map = new() { ["id"] = d.Id, ["title"] = d.Title };
        Add(map, "goal", d.Goal);
        Add(map, "exit", d.Exit ?? []);
        Add(map, "notes", d.Notes);
        return map;
    }
    private static void Add(Dictionary<string, object> map, string key, string value) { if (value.Length is not 0) map[key] = value; }
    private static void Add(Dictionary<string, object> map, string key, IReadOnlyList<string> value) { if (value.Count is not 0) map[key] = value; }

    private sealed class FirstDocumentParser(IParser inner) : IParser
    {
        private bool _ended;
        public YamlDotNet.Core.Events.ParsingEvent? Current { get; private set; }
        public bool MoveNext()
        {
            if (_ended)
            { Current = null; return false; }
            if (Current is YamlDotNet.Core.Events.DocumentEnd)
            { _ended = true; Current = new YamlDotNet.Core.Events.StreamEnd(); return true; }
            var moved = inner.MoveNext();
            Current = inner.Current;
            return moved;
        }
    }
}

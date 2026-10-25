using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Metrics;

/// <summary>Reads Microsoft.CodeAnalysis.Metrics XML: each type's methods and properties with file and name line.</summary>
public static class MetricsReport
{
    public static IReadOnlyList<MeasuredMember> Parse(string path, string root, IReadOnlyList<string> projects)
    {
        XDocument document;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            document = XDocument.Load(reader);
        }

        // A member belongs to the deepest project whose directory holds its file.
        var homes = projects
            .Select(project => (Project: project, Directory: DirectoryOf(project)))
            .OrderByDescending(home => home.Directory.Length)
            .ToArray();
        List<MeasuredMember> output = [];
        foreach (var member in document.Descendants("Members").Elements())
        {
            if (
                member.Name.LocalName is not ("Method" or "Property")
                || member.Attribute("File")?.Value is not { Length: > 0 } file
                || !int.TryParse(member.Attribute("Line")?.Value, CultureInfo.InvariantCulture, out var line)
            )
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }

            output.Add(
                new(
                    relative,
                    line,
                    Name(member.Attribute("Name")?.Value ?? ""),
                    member.Name.LocalName is "Method" ? "method" : "property",
                    Metric(member, "CyclomaticComplexity"),
                    Metric(member, "MaintainabilityIndex"),
                    homes
                        .FirstOrDefault(home =>
                            home.Directory.Length is 0
                            || relative.StartsWith(home.Directory + "/", StringComparison.Ordinal)
                        )
                        .Project
                        ?? ""
                )
            );
        }
        return output;
    }

    /// <summary>Type.Member from a signature such as List&lt;T&gt; Owner.Type.Member(int a, string b).</summary>
    public static string Name(string signature)
    {
        var text = signature.Trim();
        if (text.EndsWith(')'))
        {
            var depth = 0;
            for (var index = text.Length - 1; index >= 0; index--)
            {
                depth += text[index] switch
                {
                    ')' => 1,
                    '(' => -1,
                    _ => 0,
                };
                if (depth is 0)
                {
                    text = text[..index];
                    break;
                }
            }
        }

        // The last space outside <> and () separates a return type from the name.
        var nesting = 0;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            nesting += text[index] switch
            {
                '>' or ')' => 1,
                '<' or '(' => -1,
                _ => 0,
            };
            if (nesting is 0 && text[index] is ' ')
            {
                text = text[(index + 1)..];
                break;
            }
        }

        // Box<T>.Get<U> names Box.Get, as harness's own count does.
        var plain = new System.Text.StringBuilder(text.Length);
        var generics = 0;
        foreach (var character in text)
        {
            generics += character switch
            {
                '<' => 1,
                '>' => -1,
                _ => 0,
            };
            if (generics is 0 && character is not '>')
            {
                plain.Append(character);
            }
        }

        var parts = plain.ToString().Split('.');
        return parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
    }

    private static int Metric(XElement member, string name) =>
        member
            .Element("Metrics")
            ?.Elements("Metric")
            .FirstOrDefault(metric => metric.Attribute("Name")?.Value == name)
            ?.Attribute("Value")
            ?.Value
            is { } value
        && int.TryParse(value, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    private static string DirectoryOf(string project) =>
        Path.GetDirectoryName(project)?.Replace('\\', '/') is { Length: > 0 } directory ? directory : "";
}

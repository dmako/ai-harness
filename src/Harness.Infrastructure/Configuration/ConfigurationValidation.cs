using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Providers;

namespace Harness.Infrastructure.Configuration;

internal static class ConfigurationValidation
{
    private static readonly string[] Capabilities =
    [
        "code.symbol_search",
        "code.text_search",
        "code.file_search",
        "project.overview",
        "code.callers",
        "code.references",
        "code.context",
        "code.impact",
        "code.flow",
        "package.search",
        "package.info",
        "package.types",
        "package.type_definition",
        "arch.dependencies",
        "arch.cycles",
        "arch.change_coupling",
        "arch.duplicates",
        "arch.dead_code",
    ];
    private static readonly string[] Fields =
    [
        "name",
        "qualified_name",
        "label",
        "path",
        "start_line",
        "end_line",
        "snippet",
        "score",
        "depth",
        "related_name",
        "related_path",
        "related_line",
        "members",
        "count",
        "confidence",
    ];
    private static readonly string[] Languages = ["csharp", "typescript", "go", "python", "java"];

    internal static void Validate(HarnessConfiguration configuration)
    {
        Require(!string.IsNullOrWhiteSpace(configuration.Storage.DbPath), "storage.db_path must not be empty");
        Require(configuration.Storage.BusyTimeoutMs >= 0, "storage.busy_timeout_ms must be >= 0");
        Require(
            configuration.Log.Level is "debug" or "info" or "warn" or "error",
            "log.level must be debug, info, warn or error"
        );
        var complexity = configuration.Architecture.Complexity;
        Require(
            complexity.Note >= 1 && complexity.Note <= complexity.Warn && complexity.Warn <= complexity.Problem,
            "architecture.complexity must satisfy 1 <= note <= warn <= problem"
        );
        foreach (var (name, provider) in configuration.Providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = $"providers.{name}";
            Require(provider.Type is "mcp" or "harness", $"{key}: set pack, type = \"mcp\" or type = \"harness\"");
            Require(provider.Dir is "" or "project", $"{key}.dir must be project or empty");
            Require(
                !provider.IsEnabled() || !string.IsNullOrWhiteSpace(provider.Command),
                $"{key}.command is required"
            );
            Require(provider.TimeoutMs >= 0, $"{key}.timeout_ms must be >= 0");
            Require(
                provider.Only.All(capability => Capabilities.Contains(capability, StringComparer.Ordinal)),
                $"{key}.only: unknown capability"
            );
            Require(
                provider.Languages.All(language => Languages.Contains(language, StringComparer.Ordinal)),
                $"{key}.languages: unknown language"
            );
            if (provider.Type == "harness")
            {
                Require(
                    provider.Capabilities.Count == 0
                        && provider.Freshness.Length == 0
                        && provider.Refresh is null
                        && provider.Resolve is null
                        && !provider.IndexWorktrees,
                    $"{key}: harness providers report capabilities, freshness and refresh themselves"
                );
                continue;
            }

            Require(provider.Capabilities.Count > 0, $"{key}.capabilities: map at least one capability to a tool");
            Require(provider.Freshness is "" or "live" or "indexed", $"{key}.freshness must be live or indexed");
            Require(
                !provider.IndexWorktrees || provider.Refresh is not null && provider.Resolve is not null,
                $"{key}.index_worktrees needs refresh and resolve"
            );
            if (provider.Resolve is { } resolve)
            {
                Require(
                    !string.IsNullOrWhiteSpace(resolve.Tool)
                        && !string.IsNullOrWhiteSpace(resolve.Match)
                        && !string.IsNullOrWhiteSpace(resolve.Value),
                    $"{key}.resolve: tool, match and value are required"
                );
            }

            if (provider.Refresh is { } refresh)
            {
                Require(!string.IsNullOrWhiteSpace(refresh.Tool), $"{key}.refresh.tool is required");
                CheckRegex(refresh.Skip, $"{key}.refresh.skip");
            }

            foreach (
                var (capability, mapping) in provider.Capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            )
            {
                var mappingKey = $"{key}.capabilities.{capability}";
                Require(Capabilities.Contains(capability, StringComparer.Ordinal), $"{mappingKey}: unknown capability");
                CheckCall(mapping, mappingKey, capability);
                Require(mapping.Combine is "" or "first" or "all", $"{mappingKey}.combine must be first or all");
                foreach (var call in mapping.Chain)
                {
                    Require(
                        call.Chain.Length == 0 && call.Combine.Length == 0,
                        $"{mappingKey}.chain: nested chain/combine is not allowed"
                    );
                    CheckCall(call, $"{mappingKey}.chain", capability);
                }
            }
        }
    }

    private static void CheckCall(CapabilityMapping mapping, string key, string capability)
    {
        Require(!string.IsNullOrWhiteSpace(mapping.Tool), $"{key}.tool is required");
        Require(
            mapping.Fields.All(field =>
                Fields.Contains(field.Key, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(field.Value)
            ),
            $"{key}.fields: unknown field or empty path"
        );
        Require(mapping.Evidence is "" or "exact" or "inferred", $"{key}.evidence must be exact or inferred");
        Require(
            mapping.Kind is "" or "symbol" or "match" or "file" or "package" or "relation",
            $"{key}.kind is invalid"
        );
        Require(
            mapping.Args.Values.All(value =>
                value.ValueKind
                    is JsonValueKind.String
                        or JsonValueKind.Number
                        or JsonValueKind.True
                        or JsonValueKind.False
            ),
            $"{key}.args: only strings, numbers and booleans are supported"
        );
        if (capability is "package.info" or "package.type_definition")
        {
            Require(
                mapping.Pattern.Length == 0 && mapping.Items.Length == 0 && mapping.Fields.Count == 0,
                $"{key}: text capability cannot have items, fields or pattern"
            );
        }

        CheckRegex(mapping.Miss, $"{key}.miss");
        if (mapping.Pattern.Length > 0)
        {
            Require(
                mapping.Items.Length == 0 && mapping.Fields.Count == 0,
                $"{key}: use pattern or items/fields, not both"
            );
            var expression = CheckRegex(mapping.Pattern, $"{key}.pattern")!;
            var groups = expression.GetGroupNames().Where(group => !int.TryParse(group, out _)).ToArray();
            Require(
                groups.Length > 0 && groups.All(group => Fields.Contains(group, StringComparer.Ordinal)),
                $"{key}.pattern: named item-field groups required"
            );
            CheckRegex(mapping.Truncated, $"{key}.truncated");
        }
    }

    private static Regex? CheckRegex(string pattern, string key)
    {
        if (pattern.Length == 0)
        {
            return null;
        }

        try
        {
            return new Regex(
                pattern.Replace("(?P<", "(?<", StringComparison.Ordinal),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"{key}: invalid regular expression", exception);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

using Harness.Core.Providers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using Tomlyn.Model;
using Tomlyn;

namespace Harness.Infrastructure.Configuration;

public static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        IgnoreReadOnlyProperties = true,
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static LoadedConfiguration Load(string root) => LoadFiles(ConfigurationPaths.GlobalPath(), string.IsNullOrEmpty(root) ? "" : ConfigurationPaths.ProjectPath(root));

    public static LoadedConfiguration LoadFiles(string globalPath, string projectPath)
    {
        var effective = JsonSerializer.SerializeToNode(new HarnessConfiguration(), JsonOptions)!.AsObject();
        var sources = new string[2];
        var paths = new[] { globalPath, projectPath };
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            sources[index] = "";
            if (path.Length == 0)
            {
                continue;
            }

            try
            {
                var text = StrictUtf8.GetString(File.ReadAllBytes(path));
                var layer = Parse(text.StartsWith('\uFEFF') ? text[1..] : text);
                // Validate unknown keys and types before pack expansion can obscure the source.
                _ = layer.Deserialize<HarnessConfiguration>(JsonOptions);
                ExpandPacks(layer);
                foreach (var (key, value) in layer)
                {
                    if (effective[key] is JsonObject destination && value is JsonObject incoming)
                    {
                        foreach (var (name, setting) in incoming)
                        {
                            // This deliberately replaces a provider's entire table.
                            destination[name] = setting?.DeepClone();
                        }
                    }
                    else
                    {
                        effective[key] = value?.DeepClone();
                    }
                }

                ConfigurationValidation.Validate(effective.Deserialize<HarnessConfiguration>(JsonOptions)!);
                sources[index] = path;
            }
            catch (FileNotFoundException)
            {
                // Missing layers are optional; existing malformed layers are never ignored.
            }
            catch (DirectoryNotFoundException)
            {
                // Same semantics when the optional parent directory does not exist.
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or TomlException or ArgumentException)
            {
                throw new InvalidDataException($"config {path}: {exception.Message}", exception);
            }
        }

        return new LoadedConfiguration(effective.Deserialize<HarnessConfiguration>(JsonOptions)!, new ConfigurationSources(sources[0], sources[1]));
    }

    public static IReadOnlyList<string> Packs { get; } = typeof(ConfigurationLoader).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Harness.Packs.", StringComparison.Ordinal))
        .Select(name => name["Harness.Packs.".Length..^".toml".Length]).Order(StringComparer.Ordinal).ToArray();

    private static JsonObject Parse(string text) => JsonSerializer.SerializeToNode(TomlSerializer.Deserialize<TomlTable>(text))!.AsObject();

    private static void ExpandPacks(JsonObject layer)
    {
        if (layer["providers"] is not JsonObject providers)
        {
            return;
        }

        foreach (var (name, value) in providers.ToArray())
        {
            var table = value!.AsObject();
            var pack = table["pack"]?.GetValue<string>() ?? "";
            if (pack.Length == 0 && string.IsNullOrEmpty(table["type"]?.GetValue<string>()) && Packs.Contains(name, StringComparer.Ordinal))
            {
                pack = name;
            }

            if (pack.Length == 0)
            {
                continue;
            }

            if (!Packs.Contains(pack, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"providers.{name}.pack: unknown provider pack '{pack}'");
            }

            using var stream = typeof(ConfigurationLoader).Assembly.GetManifestResourceStream($"Harness.Packs.{pack}.toml")!;
            using var reader = new StreamReader(stream, StrictUtf8);
            var expanded = Parse(reader.ReadToEnd());
            foreach (var (key, setting) in table)
            {
                if (key == "capabilities" && expanded[key] is JsonObject capabilities)
                {
                    foreach (var (capability, mapping) in setting!.AsObject())
                    {
                        capabilities[capability] = mapping?.DeepClone();
                    }
                }
                else
                {
                    expanded[key] = setting?.DeepClone();
                }
            }

            expanded["pack"] = pack;
            providers[name] = expanded;
        }
    }
}

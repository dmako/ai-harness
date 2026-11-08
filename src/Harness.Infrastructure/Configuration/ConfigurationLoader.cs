using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Tomlyn;
using Tomlyn.Model;

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

    public static LoadedConfiguration Load(string root) =>
        LoadFiles(
            ConfigurationPaths.GlobalPath(),
            string.IsNullOrEmpty(root) ? "" : ConfigurationPaths.ProjectPath(root)
        );

    public static LoadedConfiguration LoadFiles(string globalPath, string projectPath) =>
        LoadFiles(globalPath, projectPath, null);

    /// <summary>Loads the layers, reading a file's planned text instead of the file when one is given (a dry run).</summary>
    internal static LoadedConfiguration LoadFiles(
        string globalPath,
        string projectPath,
        IReadOnlyDictionary<string, string>? planned
    )
    {
        var effective = HarnessJson.SerializeToNode(new HarnessConfiguration(), JsonOptions)!.AsObject();
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
                var text = planned?.GetValueOrDefault(path) ?? StrictUtf8.GetString(File.ReadAllBytes(path));
                var layer = Parse(text.StartsWith('\uFEFF') ? text[1..] : text);
                // Validate unknown keys and types before pack expansion can obscure the source.
                _ = HarnessJson.Deserialize<HarnessConfiguration>(layer, JsonOptions);
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

                ConfigurationValidation.Validate(
                    HarnessJson.Deserialize<HarnessConfiguration>(effective, JsonOptions)!
                );
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
            catch (Exception exception)
                when (exception
                        is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or JsonException
                            or TomlException
                            or ArgumentException
                )
            {
                throw new InvalidDataException($"config {path}: {exception.Message}", exception);
            }
        }

        return new LoadedConfiguration(
            HarnessJson.Deserialize<HarnessConfiguration>(effective, JsonOptions)!,
            new ConfigurationSources(sources[0], sources[1])
        );
    }

    public static IReadOnlyList<string> Packs { get; } =
        typeof(ConfigurationLoader)
            .Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Harness.Packs.", StringComparison.Ordinal))
            .Select(name => name["Harness.Packs.".Length..^".toml".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The model prices: harness's shipped table (Configuration/Pricing/pricing.toml) with the configuration's
    /// [pricing.&lt;model&gt;] entries over it.
    /// </summary>
    public static PriceTable Prices(HarnessConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<string, ModelPrice> prices = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, price) in ShippedPricing.Value)
        {
            prices[model] = price;
        }
        foreach (var (model, price) in configuration.Pricing)
        {
            var shipped = prices.GetValueOrDefault(model);
            var input = price.Input ?? shipped?.Input ?? 0;
            prices[model] = new(
                input,
                price.CachedInput ?? shipped?.CachedInput ?? input,
                price.CacheWrite ?? shipped?.CacheWrite ?? input,
                price.Output ?? shipped?.Output ?? 0
            );
        }
        return new(prices);
    }

    private static readonly Lazy<IReadOnlyDictionary<string, ModelPrice>> ShippedPricing = new(() =>
    {
        using var stream = typeof(ConfigurationLoader).Assembly.GetManifestResourceStream("Harness.Pricing.toml")!;
        using var reader = new StreamReader(stream, StrictUtf8);
        var shipped = HarnessJson.Deserialize<HarnessConfiguration>(Parse(reader.ReadToEnd()), JsonOptions)!;
        return shipped.Pricing.ToDictionary(
            pair => pair.Key,
            pair => new ModelPrice(
                pair.Value.Input ?? 0,
                pair.Value.CachedInput ?? 0,
                pair.Value.CacheWrite ?? 0,
                pair.Value.Output ?? 0
            ),
            StringComparer.OrdinalIgnoreCase
        );
    });

    private static JsonObject Parse(string text) =>
        HarnessJson
            .SerializeToNode(
                TomlSerializer.Deserialize(
                    text,
                    Harness.Infrastructure.Configuration.ConfigurationTomlContext.Default.TomlTable
                )
            )!
            .AsObject();

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
            if (
                pack.Length == 0
                && string.IsNullOrEmpty(table["type"]?.GetValue<string>())
                && Packs.Contains(name, StringComparer.Ordinal)
            )
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

            using var stream = typeof(ConfigurationLoader).Assembly.GetManifestResourceStream(
                $"Harness.Packs.{pack}.toml"
            )!;
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

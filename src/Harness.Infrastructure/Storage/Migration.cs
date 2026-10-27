using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Infrastructure.Storage;

internal sealed record Migration(int Version, string Name, string Sql);

internal static partial class MigrationCatalog
{
    private static readonly Lazy<IReadOnlyList<Migration>> Embedded = new(LoadEmbedded);

    [GeneratedRegex(@"\A[0-9]{4}_[a-z0-9_]+\.sql\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex FileName { get; }

    internal static IReadOnlyList<Migration> All => Embedded.Value;

    internal static IReadOnlyList<Migration> Load(IEnumerable<(string FileName, string Sql)> files)
    {
        var migrations = new List<Migration>();
        foreach (var (fileName, sql) in files)
        {
            if (!FileName.IsMatch(fileName))
            {
                throw new InvalidDataException($"Unexpected migration file '{fileName}' (want NNNN_name.sql).");
            }

            migrations.Add(
                new Migration(int.Parse(fileName.AsSpan(0, 4), CultureInfo.InvariantCulture), fileName[..^4], sql)
            );
        }

        migrations.Sort((left, right) => left.Version.CompareTo(right.Version));
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index].Version != index + 1)
            {
                throw new InvalidDataException(
                    $"Migration {migrations[index].Name}: expected version {index + 1:0000} (versions must be contiguous from 0001)."
                );
            }
        }

        return migrations.AsReadOnly();
    }

    private static IReadOnlyList<Migration> LoadEmbedded()
    {
        const string Prefix = "Harness.Migrations.";
        var assembly = typeof(MigrationCatalog).Assembly;
        return Load(
            assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(name =>
                {
                    using var stream =
                        assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidDataException($"Missing migration resource '{name}'.");
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                    return (name[Prefix.Length..], reader.ReadToEnd());
                })
        );
    }
}

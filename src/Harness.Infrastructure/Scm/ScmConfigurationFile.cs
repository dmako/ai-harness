using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;
using Harness.Infrastructure.Workspaces;
using Harness.Scm;
using Tomlyn;
using Tomlyn.Model;

namespace Harness.Infrastructure.Scm;

/// <summary>
/// The machine's SCM connections in <c>scm.toml</c> beside the global <c>config.toml</c>: one <c>[scm.&lt;name&gt;]</c>
/// table per SCM with <c>type</c>, <c>url</c> and <c>token</c> or <c>token_env</c>. Writes hold the machine-file lock,
/// go through an owner-only temporary file and replace the file atomically, so the token never sits in a file others
/// can read and a wider-permission file is tightened by the next write.
/// </summary>
public sealed class ScmConfigurationFile : IScmConfiguration
{
    public const string FileName = "scm.toml";

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<string, string?> _environment;

    public ScmConfigurationFile(string filePath, Func<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public string FilePath { get; }

    /// <summary>The store beside a global configuration file (<c>&lt;dir&gt;/config.toml</c>).</summary>
    public static ScmConfigurationFile ForGlobal(string globalConfigurationPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!, FileName));

    /// <summary>The store of the user's global configuration directory.</summary>
    public static ScmConfigurationFile Default() => ForGlobal(ConfigurationPaths.GlobalPath());

    public IReadOnlyList<ScmEntry> List() => Load().Select(View).ToList();

    public ScmEntry? Find(string name)
    {
        var record = Load().FirstOrDefault(entry => Same(entry.Name, name));
        return record is null ? null : View(record);
    }

    public async Task<ScmEntry> AddAsync(ScmEntryInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var record = new Record(
            ScmEntryRules.Name(input.Name),
            ScmEntryRules.Kind(input.Type),
            ScmEntryRules.BaseUrl(input.Address),
            null,
            null
        );
        record = (input.Token, input.TokenEnv) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => throw new ScmConfigurationException(
                "Give either a token or a token environment variable, not both."
            ),
            ({ Length: > 0 } token, _) => record with { Token = ScmEntryRules.Token(token) },
            (_, { Length: > 0 } variable) => record with { TokenEnv = ScmEntryRules.Variable(variable) },
            _ => throw new ScmConfigurationException(
                "A token is required: give it with --token-stdin (or --token), or name an environment variable with --token-env."
            ),
        };
        await UpdateFileAsync(
                records =>
                {
                    if (records.Any(entry => Same(entry.Name, record.Name)))
                    {
                        throw new ScmConfigurationException($"An SCM named '{record.Name}' already exists.");
                    }

                    records.Add(record);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return View(record);
    }

    public async Task<ScmEntry> UpdateAsync(string name, ScmEntryChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Token is { Length: > 0 } && change.TokenEnv is { Length: > 0 })
        {
            throw new ScmConfigurationException("Give either a token or a token environment variable, not both.");
        }

        Record? updated = null;
        await UpdateFileAsync(
                records =>
                {
                    var index = records.FindIndex(entry => Same(entry.Name, name));
                    if (index < 0)
                    {
                        throw new ScmConfigurationException($"No SCM is named '{name}'.");
                    }

                    var record = records[index];
                    if (change.Rename is { Length: > 0 } rename)
                    {
                        var renamed = ScmEntryRules.Name(rename);
                        if (records.Where((_, other) => other != index).Any(entry => Same(entry.Name, renamed)))
                        {
                            throw new ScmConfigurationException($"An SCM named '{renamed}' already exists.");
                        }

                        record = record with { Name = renamed };
                    }

                    if (change.Type is { Length: > 0 } type)
                    {
                        record = record with { Kind = ScmEntryRules.Kind(type) };
                    }

                    if (change.Address is { Length: > 0 } address)
                    {
                        record = record with { Url = ScmEntryRules.BaseUrl(address) };
                    }

                    if (change.Token is { Length: > 0 } token)
                    {
                        record = record with { Token = ScmEntryRules.Token(token), TokenEnv = null };
                    }

                    if (change.TokenEnv is { Length: > 0 } variable)
                    {
                        record = record with { Token = null, TokenEnv = ScmEntryRules.Variable(variable) };
                    }

                    records[index] = record;
                    updated = record;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return View(updated!);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var removed = false;
        await UpdateFileAsync(
                records => removed = records.RemoveAll(entry => Same(entry.Name, name)) > 0,
                cancellationToken
            )
            .ConfigureAwait(false);
        return removed;
    }

    public ScmConnection Resolve(string name)
    {
        var record =
            Load().FirstOrDefault(entry => Same(entry.Name, name))
            ?? throw new ScmConfigurationException($"No SCM is named '{name}'.");
        var token = Token(record);
        if (token is { Length: > 0 })
        {
            return new ScmConnection(Endpoint(record), token);
        }

        var missing = record.TokenEnv is { } variable
            ? $"SCM '{record.Name}' reads its token from the environment variable {variable}, which is not set."
            : $"SCM '{record.Name}' has no token; set one with 'harness scm set {record.Name} --token-stdin'.";
        throw new ScmConfigurationException(missing);
    }

    public ScmFileState Inspect()
    {
        if (!File.Exists(FilePath))
        {
            return new ScmFileState(FilePath, false, "", null, "");
        }

        var error = "";
        try
        {
            _ = Load();
        }
        catch (ScmConfigurationException exception)
        {
            error = exception.Message;
        }

        var (ownerOnly, detail) = Permissions(FilePath);
        return new ScmFileState(FilePath, true, error, ownerOnly, detail);
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private ScmEntry View(Record record)
    {
        var token = Token(record);
        return new ScmEntry(
            Endpoint(record),
            record.TokenEnv is null ? ScmTokenSource.Stored : ScmTokenSource.Environment,
            record.TokenEnv,
            ScmTokens.Mask(token),
            token is { Length: > 0 }
        );
    }

    private string? Token(Record record) =>
        record.TokenEnv is { } variable ? _environment(variable)?.Trim() : record.Token;

    private static ScmEndpoint Endpoint(Record record) => new(record.Name, record.Kind, record.Url);

    private List<Record> Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath, Utf8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ScmConfigurationException($"Cannot read {FilePath}: {exception.Message}", exception);
        }

        return Parse(text);
    }

    private List<Record> Parse(string text)
    {
        TomlTable document;
        try
        {
            document = TomlSerializer.Deserialize(text, ConfigurationTomlContext.Default.TomlTable)!;
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            // Tomlyn's messages quote source positions, not values; still keep them short.
            throw new ScmConfigurationException(
                $"{FilePath} is not valid TOML: {FirstLine(exception.Message)}",
                exception
            );
        }

        if (!document.TryGetValue("scm", out var section))
        {
            return [];
        }

        if (section is not TomlTable tables)
        {
            throw new ScmConfigurationException($"{FilePath}: 'scm' must be a table of [scm.<name>] tables.");
        }

        List<Record> records = [];
        foreach (var (name, value) in tables)
        {
            records.Add(ParseEntry(name, value as TomlTable));
            if (records.Count(entry => Same(entry.Name, name)) > 1)
            {
                throw new ScmConfigurationException($"{FilePath}: the SCM name '{name}' appears twice.");
            }
        }

        return records;
    }

    private Record ParseEntry(string name, TomlTable? table)
    {
        string Where(string message) => $"{FilePath}: [scm.{name}] {message}";
        if (table is null)
        {
            throw new ScmConfigurationException(Where("must be a table."));
        }

        string? Text(string key) =>
            table.TryGetValue(key, out var value)
                ? value as string ?? throw new ScmConfigurationException(Where($"'{key}' must be a string."))
                : null;
        try
        {
            var token = Text("token");
            var variable = Text("token_env");
            return new Record(
                ScmEntryRules.Name(name),
                ScmEntryRules.Kind(Text("type") ?? throw new ScmConfigurationException("'type' is missing.")),
                ScmEntryRules.BaseUrl(Text("url") ?? throw new ScmConfigurationException("'url' is missing.")),
                string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
                string.IsNullOrWhiteSpace(variable) ? null : ScmEntryRules.Variable(variable.Trim())
            );
        }
        catch (ScmConfigurationException exception)
            when (!exception.Message.StartsWith(FilePath, StringComparison.Ordinal))
        {
            throw new ScmConfigurationException(Where(exception.Message), exception);
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private async Task UpdateFileAsync(Action<List<Record>> edit, CancellationToken cancellationToken)
    {
        StorageFiles.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var lease = await AtomicMachineFile
            .LockAsync(FilePath + ".lock", cancellationToken)
            .ConfigureAwait(false);
        var records = Load();
        edit(records);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (new FileStream(temporary, options))
            { /* Created empty and restricted below before any token is written. */
            }

            PrivateExportFiles.OwnerOnly(temporary);
            await File.WriteAllBytesAsync(temporary, Utf8.GetBytes(Render(records)), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
            PrivateExportFiles.OwnerOnly(FilePath);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string Render(IEnumerable<Record> records)
    {
        var builder = new StringBuilder();
        builder.Append(
            "# harness SCM connections, written by 'harness scm' and harness ui.\n"
                + "# It holds access tokens: keep it private (readable only by you) and out of repositories.\n"
        );
        foreach (var record in records)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n[scm.{Key(record.Name)}]\n");
            builder.Append(CultureInfo.InvariantCulture, $"type = {Quote(ScmKinds.Name(record.Kind))}\n");
            builder.Append(CultureInfo.InvariantCulture, $"url = {Quote(record.Url.AbsoluteUri.TrimEnd('/'))}\n");
            if (record.TokenEnv is { } variable)
            {
                builder.Append(CultureInfo.InvariantCulture, $"token_env = {Quote(variable)}\n");
            }
            else if (record.Token is { } token)
            {
                builder.Append(CultureInfo.InvariantCulture, $"token = {Quote(token)}\n");
            }
        }

        return builder.ToString();
    }

    private static string Key(string name) => name.Contains('.', StringComparison.Ordinal) ? Quote(name) : name;

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            builder.Append(
                character switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ when char.IsControl(character) => string.Create(
                        CultureInfo.InvariantCulture,
                        $"\\u{(int)character:X4}"
                    ),
                    _ => character.ToString(),
                }
            );
        }

        return builder.Append('"').ToString();
    }

    /// <summary>Whether only the current user may read the file, and what else may when not.</summary>
    internal static (bool? OwnerOnly, string Detail) Permissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode Others =
                    UnixFileMode.GroupRead
                    | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute;
                return (mode & Others) is 0
                    ? (true, "")
                    : (false, $"mode {Convert.ToString((int)mode, 8)} lets group or others access it (want 600)");
            }

            var others = OtherWindowsAccounts(path);
            return others.Count is 0 ? (true, "") : (false, "also readable by " + string.Join(", ", others));
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or PlatformNotSupportedException
            )
        {
            return (null, "cannot read its permissions: " + exception.Message);
        }
    }

    /// <summary>The accounts other than the current user that the file's ACL allows any access.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> OtherWindowsAccounts(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User;
        List<string> others = [];
        foreach (
            var rule in new FileInfo(path)
                .GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
        )
        {
            if (rule.AccessControlType is AccessControlType.Allow && rule.IdentityReference != user)
            {
                others.Add(Account(rule.IdentityReference));
            }
        }

        return others.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static string Account(IdentityReference identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or SystemException)
        {
            return identity.Value;
        }
    }

    internal sealed record Record(string Name, ScmKind Kind, Uri Url, string? Token, string? TokenEnv);
}

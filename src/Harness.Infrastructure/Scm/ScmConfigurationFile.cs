using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Harness.Core.Projects;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;
using Harness.Infrastructure.Workspaces;
using Harness.Scm;
using Tomlyn;
using Tomlyn.Model;

namespace Harness.Infrastructure.Scm;

/// <summary>
/// The machine's SCM connections in <c>scm.toml</c> beside the global <c>config.toml</c>: one <c>[scm.&lt;name&gt;]</c>
/// table per SCM with <c>type</c>, <c>url</c> and <c>token_protected</c> (the token encrypted by
/// <see cref="ScmTokenProtector" />) or <c>token_env</c>. A plain <c>token</c> written before tokens were encrypted is
/// still read, and encrypted by the next write. Writes hold the machine-file lock, go through an owner-only temporary
/// file and replace the file atomically, so a wider-permission file is tightened by the next write.
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
        Protector = ScmTokenProtector.ForFile(FilePath);
    }

    public string FilePath { get; }

    /// <summary>Encrypts and decrypts the stored tokens, with its key folder beside the file.</summary>
    public ScmTokenProtector Protector { get; }

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
        var saved = await UpdateFileAsync(
                (records, _) =>
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
        return Saved(saved, record.Name);
    }

    public async Task<ScmEntry> UpdateAsync(string name, ScmEntryChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Token is { Length: > 0 } && change.TokenEnv is { Length: > 0 })
        {
            throw new ScmConfigurationException("Give either a token or a token environment variable, not both.");
        }

        Record? updated = null;
        var saved = await UpdateFileAsync(
                (records, choices) =>
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

                        // the repositories that chose it keep it under its new name
                        foreach (var repository in choices.Where(choice => Same(choice.Value, record.Name)).ToList())
                        {
                            choices[repository.Key] = renamed;
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
                        record = record with { Token = ScmEntryRules.Token(token), Protected = null, TokenEnv = null };
                    }

                    if (change.TokenEnv is { Length: > 0 } variable)
                    {
                        record = record with
                        {
                            Token = null,
                            Protected = null,
                            TokenEnv = ScmEntryRules.Variable(variable),
                        };
                    }

                    records[index] = record;
                    updated = record;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return Saved(saved, updated!.Name);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var removed = false;
        await UpdateFileAsync(
                (records, choices) =>
                {
                    removed = records.RemoveAll(entry => Same(entry.Name, name)) > 0;
                    // the repositories that chose it go back to matching their remotes
                    foreach (var repository in choices.Where(choice => Same(choice.Value, name)).ToList())
                    {
                        choices.Remove(repository.Key);
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return removed;
    }

    public string? ChoiceFor(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return LoadAll().Choices.GetValueOrDefault(PhysicalKey(repository));
    }

    public async Task SetChoiceAsync(string repository, string? name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var key = PhysicalKey(repository);
        await UpdateFileAsync(
                (records, choices) =>
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        choices.Remove(key);
                        return;
                    }

                    var record =
                        records.FirstOrDefault(entry => Same(entry.Name, name.Trim()))
                        ?? throw new ScmConfigurationException(
                            $"No SCM is named '{name.Trim()}' (configured: "
                                + (records.Count is 0 ? "none" : string.Join(", ", records.Select(entry => entry.Name)))
                                + ")."
                        );
                    choices[key] = record.Name;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>A stored repository key as written: a full path without a trailing separator.</summary>
    private static string RepositoryKey(string repository) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));

    /// <summary>
    /// The key of a repository named by a caller: its common git directory in its physical, on-disk spelling, the way
    /// <see cref="GitRepository" /> reports it, so a path through a symbolic link (macOS's /var for /private/var, a
    /// linked checkout) or in another case on Windows names the same repository. A directory that cannot be resolved
    /// keeps its full path.
    /// </summary>
    private static string PhysicalKey(string repository)
    {
        var full = RepositoryKey(repository);
        try
        {
            return Directory.Exists(full)
                ? Path.TrimEndingDirectorySeparator(GitRepository.PhysicalDirectory(full))
                : full;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    public ScmConnection Resolve(string name)
    {
        var record =
            Load().FirstOrDefault(entry => Same(entry.Name, name))
            ?? throw new ScmConfigurationException($"No SCM is named '{name}'.");
        var token = Token(record); // throws, naming the fix, when a stored token cannot be decrypted
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
        var (keysExist, keysOwnerOnly, keysDetail) = Protector.Inspect();
        if (!File.Exists(FilePath))
        {
            return new ScmFileState(
                FilePath,
                false,
                "",
                null,
                "",
                Protector.KeyDirectory,
                keysExist,
                keysOwnerOnly,
                keysDetail
            );
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
        return new ScmFileState(
            FilePath,
            true,
            error,
            ownerOnly,
            detail,
            Protector.KeyDirectory,
            keysExist,
            keysOwnerOnly,
            keysDetail
        );
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private ScmEntry View(Record record)
    {
        string? token;
        string? error = null;
        try
        {
            token = Token(record);
        }
        catch (ScmConfigurationException exception)
        {
            (token, error) = (null, exception.Message);
        }

        return new ScmEntry(
            Endpoint(record),
            record.TokenEnv is null ? ScmTokenSource.Stored : ScmTokenSource.Environment,
            record.TokenEnv,
            ScmTokens.Mask(token),
            token is { Length: > 0 },
            record is { TokenEnv: null, Protected: null, Token: not null },
            error
        );
    }

    /// <summary>The token a call would use: the variable's value, the plain token, or the stored one decrypted.</summary>
    private string? Token(Record record) =>
        record switch
        {
            { TokenEnv: { } variable } => _environment(variable)?.Trim(),
            { Token: { } plain } => plain,
            { Protected: { } payload } => Protector.Unprotect(payload, record.Name),
            _ => null,
        };

    private static ScmEndpoint Endpoint(Record record) => new(record.Name, record.Kind, record.Url);

    private List<Record> Load() => LoadAll().Records;

    /// <summary>The SCMs, and the SCM chosen per repository (keyed by common git directory, as paths compare).</summary>
    private (List<Record> Records, Dictionary<string, string> Choices) LoadAll()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath, Utf8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ([], new Dictionary<string, string>(PathIdentity.Comparer));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ScmConfigurationException($"Cannot read {FilePath}: {exception.Message}", exception);
        }

        return Parse(text);
    }

    private (List<Record> Records, Dictionary<string, string> Choices) Parse(string text)
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

        List<Record> records = [];
        if (document.TryGetValue("scm", out var section))
        {
            if (section is not TomlTable tables)
            {
                throw new ScmConfigurationException($"{FilePath}: 'scm' must be a table of [scm.<name>] tables.");
            }

            foreach (var (name, value) in tables)
            {
                records.Add(ParseEntry(name, value as TomlTable));
                if (records.Count(entry => Same(entry.Name, name)) > 1)
                {
                    throw new ScmConfigurationException($"{FilePath}: the SCM name '{name}' appears twice.");
                }
            }
        }

        return (records, ParseChoices(document));
    }

    /// <summary>
    /// The <c>[project."&lt;common git dir&gt;"]</c> tables: the SCM chosen for each repository on this machine. A
    /// choice naming no configured SCM is kept, so resolution can report it instead of silently matching another.
    /// </summary>
    private Dictionary<string, string> ParseChoices(TomlTable document)
    {
        var choices = new Dictionary<string, string>(PathIdentity.Comparer);
        if (!document.TryGetValue("project", out var section))
        {
            return choices;
        }

        if (section is not TomlTable projects)
        {
            throw new ScmConfigurationException(
                $"{FilePath}: 'project' must be a table of [project.\"<repository>\"] tables."
            );
        }

        foreach (var (repository, value) in projects)
        {
            if (value is not TomlTable table || !table.TryGetValue("scm", out var name) || name is not string chosen)
            {
                throw new ScmConfigurationException(
                    $"{FilePath}: [project.\"{repository}\"] must have scm = \"<name>\"."
                );
            }

            if (chosen.Trim().Length > 0)
            {
                choices[RepositoryKey(repository)] = chosen.Trim();
            }
        }

        return choices;
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
            var encrypted = Text("token_protected");
            var variable = Text("token_env");
            var payload = string.IsNullOrWhiteSpace(encrypted) ? null : encrypted.Trim();
            return new Record(
                ScmEntryRules.Name(name),
                ScmEntryRules.Kind(Text("type") ?? throw new ScmConfigurationException("'type' is missing.")),
                ScmEntryRules.BaseUrl(Text("url") ?? throw new ScmConfigurationException("'url' is missing.")),
                // an encrypted token wins over a plain one left beside it; the next write drops the plain one
                payload is null
                && !string.IsNullOrWhiteSpace(token)
                    ? token.Trim()
                    : null,
                string.IsNullOrWhiteSpace(variable) ? null : ScmEntryRules.Variable(variable.Trim()),
                payload
            );
        }
        catch (ScmConfigurationException exception)
            when (!exception.Message.StartsWith(FilePath, StringComparison.Ordinal))
        {
            throw new ScmConfigurationException(Where(exception.Message), exception);
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    /// <summary>
    /// Applies an edit to the SCMs and the repositories' choices under the lock and writes the file; returns the
    /// records as saved (tokens encrypted).
    /// </summary>
    private async Task<List<Record>> UpdateFileAsync(
        Action<List<Record>, Dictionary<string, string>> edit,
        CancellationToken cancellationToken
    )
    {
        StorageFiles.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var lease = await AtomicMachineFile
            .LockAsync(FilePath + ".lock", cancellationToken)
            .ConfigureAwait(false);
        var (records, choices) = LoadAll();
        edit(records, choices);
        // every stored token leaves this method encrypted, the ones read in plain text included; under the lock, so
        // only one process at a time may create the first key. A write also tightens a key folder others could open.
        if (records.Any(record => record.TokenEnv is null && (record.Token ?? record.Protected) is not null))
        {
            Protector.EnsureKeyDirectory();
        }

        for (var index = 0; index < records.Count; index++)
        {
            if (records[index] is { TokenEnv: null, Protected: null, Token: { } plain })
            {
                records[index] = records[index] with { Protected = Protector.Protect(plain) };
            }
        }

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
            await File.WriteAllBytesAsync(temporary, Utf8.GetBytes(Render(records, choices)), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
            PrivateExportFiles.OwnerOnly(FilePath);
        }
        finally
        {
            File.Delete(temporary);
        }

        return records;
    }

    /// <summary>A record as saved: encrypted, with its plain token still in hand, so showing it decrypts nothing.</summary>
    private ScmEntry Saved(List<Record> records, string name) => View(records.First(entry => Same(entry.Name, name)));

    internal static string Render(IEnumerable<Record> records, IReadOnlyDictionary<string, string>? choices = null)
    {
        var builder = new StringBuilder();
        builder.Append(
            "# harness SCM connections, written by 'harness scm' and harness ui.\n"
                + "# Tokens are encrypted with the keys in scm-keys/ beside this file: back up and move both together.\n"
                + "# Keep it private (readable only by you) and out of repositories.\n"
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
            else if (record.Protected is { } payload)
            {
                builder.Append(CultureInfo.InvariantCulture, $"token_protected = {Quote(payload)}\n");
            }
            else if (record.Token is not null)
            {
                throw new InvalidOperationException(
                    $"SCM '{record.Name}': a token must be encrypted before it is written."
                );
            }
        }

        // the SCM chosen per repository on this machine ('harness scm use', harness ui)
        foreach (
            var (repository, name) in (choices ?? new Dictionary<string, string>()).OrderBy(
                choice => choice.Key,
                StringComparer.Ordinal
            )
        )
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n[project.{Quote(repository)}]\n");
            builder.Append(CultureInfo.InvariantCulture, $"scm = {Quote(name)}\n");
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

    /// <summary>Whether only the current user may read the file (or folder), and what else may when not.</summary>
    internal static (bool? OwnerOnly, string Detail) Permissions(string path, bool directory = false)
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
                var want = directory ? "700" : "600";
                return (mode & Others) is 0
                    ? (true, "")
                    : (false, $"mode {Convert.ToString((int)mode, 8)} lets group or others access it (want {want})");
            }

            var others = OtherWindowsAccounts(path, directory);
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
    private static List<string> OtherWindowsAccounts(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User;
        List<string> others = [];
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        foreach (
            var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
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

    /// <param name="Token">The token in plain text: just entered, or read from a file written before encryption.</param>
    /// <param name="Protected">The token as stored, encrypted by <see cref="ScmTokenProtector" />.</param>
    internal sealed record Record(
        string Name,
        ScmKind Kind,
        Uri Url,
        string? Token,
        string? TokenEnv,
        string? Protected
    );
}

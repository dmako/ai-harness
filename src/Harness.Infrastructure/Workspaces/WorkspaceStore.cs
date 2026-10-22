using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Projects;

namespace Harness.Infrastructure.Workspaces;

public sealed class WorkspaceStore(string globalConfigurationPath, MachineRegistry registry)
{
    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!,
        "workspaces.json"
    );
    private static readonly Regex Name = new(
        @"\A[a-z0-9][a-z0-9._-]{0,63}\z",
        RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1)
    );

    private JsonObject LoadFile() =>
        AtomicMachineFile.Load(_path, () => new() { ["version"] = 1, ["workspaces"] = new JsonArray() });

    public IReadOnlyList<Workspace> Load() => Workspaces(LoadFile()).OfType<JsonObject>().Select(Read).ToArray();

    public Workspace Find(string name) =>
        Load().FirstOrDefault(workspace => workspace.Name == name)
        ?? throw new InvalidOperationException("no such workspace " + JsonSerializer.Serialize(name));

    private static JsonArray Workspaces(JsonObject file)
    {
        file["workspaces"] ??= new JsonArray();
        var rows = file["workspaces"]!.AsArray();
        if (rows.Any(row => row is not JsonObject))
        {
            throw new InvalidDataException("Workspaces must be an array of objects.");
        }

        foreach (var row in rows.OfType<JsonObject>())
        {
            _ = Read(row);
        }

        return rows;
    }

    private static Workspace Read(JsonObject row) =>
        new(
            row["id"]?.GetValue<string>() ?? "",
            row["name"]?.GetValue<string>() ?? "",
            row["title"]?.GetValue<string>() ?? "",
            row["members"]?.AsArray().Select(node => node!.GetValue<string>()).ToArray() ?? [],
            row["created"]?.GetValue<string>() ?? ""
        );

    private Task<bool> EditAsync(Action<JsonArray> edit, CancellationToken token) =>
        AtomicMachineFile.UpdateAsync(
            _path,
            LoadFile,
            file =>
            {
                edit(Workspaces(file));
                file["version"] = 1;
                return true;
            },
            token
        );

    public async Task<Workspace> CreateAsync(string name, CancellationToken token = default)
    {
        name = NormalizeName(name);
        Workspace? result = null;
        await EditAsync(
                rows =>
                {
                    if (rows.Any(row => row!["name"]!.GetValue<string>() == name))
                    {
                        throw new InvalidOperationException(
                            "workspace " + JsonSerializer.Serialize(name) + " already exists"
                        );
                    }

                    var row = new JsonObject
                    {
                        ["id"] = "ws_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                        ["name"] = name,
                        ["members"] = new JsonArray(),
                        ["created"] = DateTimeOffset.UtcNow.ToString(
                            "yyyy-MM-dd'T'HH:mm:ss'Z'",
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                    };
                    rows.Add(row);
                    result = Read(row);
                },
                token
            )
            .ConfigureAwait(false);
        return result!;
    }

    public async Task<IReadOnlyList<string>> JoinAsync(
        string name,
        IReadOnlyList<string> ids,
        CancellationToken token = default
    )
    {
        List<string> added = [];
        await EditAsync(
                rows =>
                {
                    var row = Row(rows, name);
                    row["members"] ??= new JsonArray();
                    var members = row["members"]!.AsArray();
                    foreach (var id in ids.Where(id => !members.Any(member => member!.GetValue<string>() == id)))
                    {
                        members.Add(id);
                        added.Add(id);
                    }
                },
                token
            )
            .ConfigureAwait(false);
        return added;
    }

    public async Task<bool> LeaveAsync(string name, string id, CancellationToken token = default)
    {
        var removed = false;
        await EditAsync(
                rows =>
                {
                    var row = Row(rows, name);
                    var members = row["members"]?.AsArray();
                    if (members is null)
                    {
                        return;
                    }

                    foreach (var member in members.Where(member => member!.GetValue<string>() == id).ToArray())
                    {
                        members.Remove(member);
                        removed = true;
                    }
                },
                token
            )
            .ConfigureAwait(false);
        return removed;
    }

    public Task DeleteAsync(string name, CancellationToken token = default) =>
        EditAsync(rows => rows.Remove(Row(rows, name)), token);

    public async Task<Workspace> RenameAsync(
        string name,
        string newName,
        string? title,
        CancellationToken token = default
    )
    {
        if (newName.Trim().Length > 0)
        {
            newName = NormalizeName(newName);
        }
        else
        {
            newName = "";
        }

        title = NormalizeTitle(title);
        Workspace? result = null;
        await EditAsync(
                rows =>
                {
                    var row = Row(rows, name);
                    if (
                        newName.Length > 0
                        && newName != name
                        && rows.Any(row => row!["name"]!.GetValue<string>() == newName)
                    )
                    {
                        throw new InvalidOperationException(
                            "workspace " + JsonSerializer.Serialize(newName) + " already exists"
                        );
                    }

                    if (newName.Length > 0)
                    {
                        row["name"] = newName;
                    }

                    if (title is not null)
                    {
                        if (title.Length is 0)
                        {
                            row.Remove("title");
                        }
                        else
                        {
                            row["title"] = title;
                        }
                    }
                    result = Read(row);
                },
                token
            )
            .ConfigureAwait(false);
        return result!;
    }

    public IReadOnlyList<WorkspaceMember> Members(string projectId)
    {
        var ids = Load()
            .Where(workspace => workspace.Members.Contains(projectId, StringComparer.Ordinal))
            .SelectMany(workspace => workspace.Members)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Resolve(ids);
    }

    public IReadOnlyList<WorkspaceMember> Resolve(IReadOnlyList<string> ids)
    {
        var projects = registry.Load()["projects"]!.AsArray().OfType<JsonObject>().ToArray();
        var members = ids.SelectMany(id =>
                projects
                    .Where(row => row["id"]?.GetValue<string>() == id)
                    .Select(row => new WorkspaceMember(
                        ShortName(row),
                        id,
                        row["root"]?.GetValue<string>() ?? "",
                        row["db_path"]?.GetValue<string>() ?? "",
                        row["title"]?.GetValue<string>() ?? ""
                    ))
            )
            .ToArray();
        var duplicates = members
            .GroupBy(member => member.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return members
            .Select(member =>
                duplicates.Contains(member.Name)
                    ? member with
                    {
                        Name = Path.GetFileName(Path.GetDirectoryName(member.Root)) + "/" + member.Name,
                    }
                    : member
            )
            .OrderBy(member => member.Name, WorkspaceNameComparer.Instance)
            .ToArray();
    }

    public static string ShortName(JsonObject project) =>
        project["name"]?.GetValue<string>() is { Length: > 0 } name
            ? name
            : Path.GetFileName(project["root"]?.GetValue<string>() ?? "");

    public IReadOnlyList<JsonObject> Discover(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory))
        {
            directory = new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName ?? directory;
        }

        return registry.Load()["projects"]!
            .AsArray()
            .OfType<JsonObject>()
            .Where(row =>
                row["root"]?.GetValue<string>() is { } root
                && (
                    root == directory
                    || root.StartsWith(
                        Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                )
            )
            .ToArray();
    }

    private static JsonObject Row(JsonArray rows, string name) =>
        rows.OfType<JsonObject>().FirstOrDefault(row => row["name"]?.GetValue<string>() == name)
        ?? throw new InvalidOperationException("no such workspace " + JsonSerializer.Serialize(name));

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Workspace short names are lowercase in the persisted source contract."
    )]
    private static string NormalizeName(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (!Name.IsMatch(name))
        {
            throw new InvalidOperationException(
                "workspace name "
                    + JsonSerializer.Serialize(name)
                    + ": use lowercase letters, digits, '.', '_' or '-' (max 64)"
            );
        }

        return name;
    }

    internal static string? NormalizeTitle(string? title)
    {
        if (title is null)
        {
            return null;
        }

        title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var length = title.EnumerateRunes().Count();
        if (length > 80)
        {
            throw new InvalidOperationException($"display name is {length} characters (max 80)");
        }

        return title;
    }
}

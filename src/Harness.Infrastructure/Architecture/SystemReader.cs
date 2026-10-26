using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Retrieval;

namespace Harness.Infrastructure.Architecture;

public sealed record SystemMember(
    string Id,
    string Name,
    string ShortName,
    AnalysisRepository Repository,
    bool Library = false
);

/// <summary>A request-scoped file reader. Addresses are matched by declared names, never probed.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "System identities and matching use lowercase normalized tokens."
)]
public sealed partial class SystemReader(IReadOnlyList<SystemMember> members)
{
    private readonly Dictionary<string, SystemNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To, string Style), SystemLink> _links = [];
    private readonly Dictionary<string, List<(string Member, string At)>> _databases = new(StringComparer.Ordinal);
    private readonly List<SystemContract> _contracts = [];
    private static readonly Regex Interface = Rx(@"\binterface\s+(I\w+)");
    private static readonly Regex Class = Rx(@"\bclass\s+(\w+)[^{;:]*?:\s*([^{;]+)");
    private static readonly Regex ClassWord = Rx(@"\bclass\s+\w+");
    private static readonly Regex Proto = Rx(@"^\s*service\s+(\w+)\s*\{");
    private static readonly Regex ContextBase = Rx(@"(?:^|\.)\w*(?:DbContext|Context)\b");
    private static readonly Regex DatabaseWords = Rx(
        @"^(?:Base|Migration|Migrated|Runtime|Design|Pooled)|(?:Base|Migration|Migrated|Runtime|Design|Pooled)$"
    );
    private static readonly Regex ConnectionKey = Rx("GetConnectionString\\(\\s*\"(\\w+)\"");
    private static readonly Regex Plumbing = Rx(
        @"keycloak|auth|issuer|authority|oidc|openid|kestrel|swagger|openapi|sentry|logging|serilog|otel|telemetry|redirect|callback|cors|origins|detailsurl|frontend|appurl|rooturl|publicurl|selfurl|baseurlpath"
    );
    private static readonly Regex UrlHost = Rx(@"^[a-z][a-z0-9+.-]*://([^/:?#]+)");
    private static readonly string[] Suffixes =
    [
        "webapi",
        "grpc",
        "api",
        "service",
        "services",
        "client",
        "server",
        "ui",
        "web",
        "app",
        "url",
        "baseurl",
        "dev",
        "svc",
    ];

    public WorkspaceSystem Read()
    {
        _nodes.Clear();
        _links.Clear();
        _databases.Clear();
        _contracts.Clear();
        foreach (var member in members)
        {
            var ecosystem = new EcosystemReader(member.Repository);
            var frontend =
                member.Repository.Files.Contains("package.json", StringComparer.Ordinal)
                && ecosystem
                    .Dependencies("package.json")
                    .Any(dependency =>
                        dependency.Name
                            is "react"
                                or "vue"
                                or "@angular/core"
                                or "svelte"
                                or "solid-js"
                                or "next"
                                or "nuxt"
                    );
            Node(
                new(
                    member.Id,
                    member.Name,
                    (frontend, member.Library) switch
                    {
                        (true, _) => "frontend",
                        (_, true) => "library",
                        _ => "service",
                    },
                    member.Id
                )
            );
        }
        ReadContracts();
        foreach (var member in members)
        {
            foreach (var file in member.Repository.Files.Where(file => !TestPath(file)))
            {
                ReadConfiguration(member, file);
            }
        }

        var findings = Findings();
        var units = _nodes
            .Values.Where(node => node.Kind is "service" or "frontend")
            .Select(node =>
            {
                var unit = new BuildUnit { Name = node.Name, Path = node.Id };
                unit.References.AddRange(
                    _links
                        .Values.Where(link =>
                            link.From == node.Id
                            && link.Style is "grpc" or "http"
                            && members.Any(member => member.Id == link.To)
                        )
                        .Select(link => link.To)
                );
                return unit;
            })
            .ToArray();
        foreach (var unit in ArchitectureAnalyzer.Analyze(new("", "", [], units)).Projects)
        {
            _nodes[unit.Path].Layer = unit.Layer;
        }

        var notes = members
            .SelectMany(member => member.Repository.Notes.Select(note => member.Name + ": " + note))
            .ToArray();
        return new(
            _nodes
                .Values.OrderBy(node =>
                    node.Kind switch
                    {
                        "frontend" => 0,
                        "service" => 1,
                        "external" => 2,
                        "broker" => 3,
                        "database" => 4,
                        _ => 0,
                    }
                )
                .ThenBy(node => node.Name, StringComparer.Ordinal)
                .ToArray(),
            _links.Values.ToArray(),
            _contracts.ToArray(),
            findings,
            notes.Length is 0 ? null : notes
        );
    }

    /// <summary>
    /// The declared contracts, and per contract where members implement it and where each member uses it.
    /// </summary>
    private sealed class ContractUsage(Dictionary<string, (string Owner, string At)> declarations)
    {
        public Dictionary<string, (string Owner, string At)> Declarations { get; } = declarations;

        public Regex Word { get; } =
            Rx(
                @"\b("
                    + string.Join('|', declarations.Keys.Order(StringComparer.Ordinal).Select(Regex.Escape))
                    + @")\b"
            );

        public Dictionary<string, List<(string Member, string At)>> Implementations { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, Dictionary<string, List<string>>> Uses { get; } = new(StringComparer.Ordinal);

        public void Implement(string name, string member, string at)
        {
            if (!Implementations.TryGetValue(name, out var places))
            {
                places = [];
                Implementations.Add(name, places);
            }
            places.Add((member, at));
        }

        public void Use(string name, string member, string at)
        {
            if (!Uses.TryGetValue(name, out var byMember))
            {
                byMember = new(StringComparer.Ordinal);
                Uses.Add(name, byMember);
            }
            if (!byMember.TryGetValue(member, out var places))
            {
                places = [];
                byMember.Add(member, places);
            }
            places.Add(at);
        }
    }

    private void ReadContracts()
    {
        var declarations = ReadDeclarations();
        if (declarations.Count is 0)
        {
            return; // Source only inspects DbContexts during its contract pass.
        }

        var usage = new ContractUsage(declarations);
        foreach (var member in members)
        {
            foreach (
                var file in member.Repository.Files.Where(file => !TestPath(file) && Path.GetExtension(file) is ".cs")
            )
            {
                ReadContractUses(member, file, usage);
            }
        }

        foreach (var name in declarations.Keys.Order(StringComparer.Ordinal))
        {
            AddContract(name, usage);
        }
    }

    /// <summary>The gRPC services of .proto files and the [ServiceContract] interfaces of C# files.</summary>
    private Dictionary<string, (string Owner, string At)> ReadDeclarations()
    {
        Dictionary<string, (string Owner, string At)> declarations = new(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (
                var file in member.Repository.Files.Where(file =>
                    !TestPath(file) && Path.GetExtension(file) is ".cs" or ".proto"
                )
            )
            {
                if (Path.GetExtension(file) is ".proto")
                {
                    ProtoDeclarations(member, file, declarations);
                }
                else
                {
                    ServiceContractDeclarations(member, file, declarations);
                }
            }
        }
        return declarations;
    }

    private static void ProtoDeclarations(
        SystemMember member,
        string file,
        Dictionary<string, (string Owner, string At)> declarations
    ) =>
        Each(
            member,
            file,
            (line, text) =>
            {
                if (Proto.Match(text) is { Success: true } proto)
                {
                    declarations[proto.Groups[1].Value] = (member.Id, At(file, line));
                }
            }
        );

    /// <summary>An interface within six lines after a [ServiceContract] attribute.</summary>
    private static void ServiceContractDeclarations(
        SystemMember member,
        string file,
        Dictionary<string, (string Owner, string At)> declarations
    )
    {
        var pending = 0;
        Each(
            member,
            file,
            (line, text) =>
            {
                if (text.Contains("[ServiceContract", StringComparison.Ordinal))
                {
                    pending = 6;
                }

                if (pending-- > 0 && Interface.Match(text) is { Success: true } match)
                {
                    declarations[match.Groups[1].Value] = (member.Id, At(file, line));
                    pending = 0;
                }
            }
        );
    }

    /// <summary>
    /// Reads a C# file statement by statement, joining class declarations that span lines (up to 2000
    /// characters), and records the contracts each one implements or uses.
    /// </summary>
    private void ReadContractUses(SystemMember member, string file, ContractUsage usage)
    {
        var pending = "";
        var pendingAt = 0;
        Each(
            member,
            file,
            (line, original) =>
            {
                var text = original.Trim();
                if (
                    text.StartsWith("//", StringComparison.Ordinal)
                    || text.StartsWith('*')
                    || text.StartsWith("using ", StringComparison.Ordinal)
                )
                {
                    return;
                }

                if (pending.Length is 0 && ClassWord.IsMatch(original) && original.IndexOfAny(['{', ';']) < 0)
                {
                    pending = original;
                    pendingAt = line;
                    return;
                }
                if (pending.Length is 0)
                {
                    ContractUses(member, file, line, original, usage);
                    return;
                }

                pending += " " + text;
                if (text.IndexOfAny(['{', ';']) < 0 && pending.Length < 2000)
                {
                    return;
                }

                ContractUses(member, file, pendingAt, pending, usage);
                pending = "";
            }
        );
    }

    /// <summary>
    /// A class declaration registers its DbContext and the contracts it implements, and only its
    /// constructor parameters count as uses; any other non-interface text counts whole.
    /// </summary>
    private void ContractUses(SystemMember member, string file, int line, string declaration, ContractUsage usage)
    {
        var match = Class.Match(Parentheses(declaration, false));
        var usedText = declaration;
        if (match.Success)
        {
            DbContext(member, file, line, match.Groups[1].Value, match.Groups[2].Value, declaration);
            foreach (var baseType in match.Groups[2].Value.Split(','))
            {
                var name = baseType.Trim().Split(new char[] { '<', ' ' }, StringSplitOptions.None)[0];
                if (usage.Declarations.ContainsKey(name))
                {
                    usage.Implement(name, member.Id, At(file, line));
                }
            }
            usedText = Parentheses(declaration, true);
        }
        if (Interface.IsMatch(usedText))
        {
            return;
        }

        foreach (var use in usage.Word.Matches(usedText).Select(match => match.Value))
        {
            usage.Use(use, member.Id, At(file, line));
        }
    }

    /// <summary>
    /// Links every member that uses the contract (other than its owner and its servers) to each server,
    /// and records the contract.
    /// </summary>
    private void AddContract(string name, ContractUsage usage)
    {
        var declaration = usage.Declarations[name];
        var servers = (usage.Implementations.GetValueOrDefault(name) ?? [])
            .DistinctBy(place => place.Member, StringComparer.Ordinal)
            .ToDictionary(place => place.Member, place => place.At, StringComparer.Ordinal);
        List<string> clients = [];
        foreach (var (client, places) in usage.Uses.GetValueOrDefault(name) ?? [])
        {
            if (servers.ContainsKey(client) || client == declaration.Owner)
            {
                continue;
            }

            clients.Add(client);
            foreach (var (server, at) in servers)
            {
                Link(
                    new(client, server, "grpc")
                    {
                        What = [name],
                        Contracts = [name],
                        Uses = places.Count,
                        Evidence = [Place(client, places[0]) + " → " + Place(server, at) + " (" + name + ")"],
                    }
                );
            }
        }
        _contracts.Add(
            new(
                name,
                declaration.Owner,
                declaration.At,
                servers.Keys.Order(StringComparer.Ordinal).ToArray(),
                clients.Order(StringComparer.Ordinal).ToArray()
            )
        );
    }

    private void DbContext(SystemMember member, string file, int line, string name, string bases, string declaration)
    {
        if (!name.EndsWith("Context", StringComparison.Ordinal) || !ContextBase.IsMatch(bases.Split(',')[0].Trim()))
        {
            return;
        }

        var logical = name[..^7];
        if (logical.EndsWith("Db", StringComparison.Ordinal))
        {
            logical = logical[..^2];
        }

        string previous;
        do
        {
            previous = logical;
            logical = DatabaseWords.Replace(logical, "");
        } while (previous != logical);
        if (logical.Length is 0)
        {
            logical = "Main";
        }

        var text = member.Repository.Read(file);
        var key = First(ConnectionKey, text);
        var connection = key.Length > 0 ? "connection " + key : null;
        var detail =
            Parentheses(declaration, true).Contains("Tenant", StringComparison.Ordinal)
            || text.Contains("TenantConnection", StringComparison.Ordinal)
            || text.Contains("ITenantConnectionStringProvider", StringComparison.Ordinal)
                ? "per tenant"
                : connection;
        var id = "db:" + member.Id + ":" + logical.ToLowerInvariant();
        if (_nodes.TryGetValue(id, out var node))
        {
            if (node.Detail is null || detail is "per tenant")
            {
                node.Detail = detail;
            }
        }
        else
        {
            Node(new(id, logical, "database", member.Id, detail));
        }

        Link(new(member.Id, id, "database") { What = [name], Evidence = [Place(member.Id, At(file, line))] });
    }

    private void Node(SystemNode node) => _nodes.TryAdd(node.Id, node);

    private void Link(SystemLink link)
    {
        if (link.From == link.To)
        {
            return;
        }

        var key = (link.From, link.To, link.Style);
        if (!_links.TryGetValue(key, out var existing))
        {
            _links.Add(key, link);
            return;
        }
        foreach (var what in link.What.Where(what => !existing.What.Contains(what, StringComparer.Ordinal)))
        {
            existing.What.Add(what);
        }

        if (link.Contracts is not null)
        {
            foreach (var contract in link.Contracts)
            {
                existing.Contracts ??= [];
                if (!existing.Contracts.Contains(contract, StringComparer.Ordinal))
                {
                    existing.Contracts.Add(contract);
                }
            }
        }

        existing.Uses += link.Uses;
        foreach (var evidence in link.Evidence)
        {
            if (existing.Evidence.Count < 8 && !existing.Evidence.Contains(evidence, StringComparer.Ordinal))
            {
                existing.Evidence.Add(evidence);
            }
        }

        existing.Basis ??= link.Basis;
    }

    private string Place(string member, string at) => members.First(item => item.Id == member).Name + ": " + at;

    private static void Each(SystemMember member, string file, Action<int, string> action)
    {
        var lines = AnalysisRepository.Lines(member.Repository.Read(file));
        for (var index = 0; index < lines.Length; index++)
        {
            action(index + 1, lines[index]);
        }
    }

    private static string At(string file, int line) => file + ":" + line.ToString(CultureInfo.InvariantCulture);

    private static Regex Rx(string pattern) => EcosystemReader.Rx(pattern);

    private static string First(Regex regex, string text) =>
        regex.Match(text) is { Success: true } match ? match.Groups[1].Value.Trim() : "";

    private static string Norm(string text) =>
        new(text.ToLowerInvariant().Where(character => character is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());

    private static string Token(string text)
    {
        var token = Norm(text);
        string previous;
        do
        {
            previous = token;
            foreach (var suffix in Suffixes)
            {
                if (token.Length > suffix.Length + 2 && token.EndsWith(suffix, StringComparison.Ordinal))
                {
                    token = token[..^suffix.Length];
                }
            }
        } while (token != previous);
        return token;
    }

    private string? MatchMember(params string[] names)
    {
        foreach (var name in names)
        {
            var token = Token(name);
            if (token.Length < 4)
            {
                continue;
            }

            foreach (var member in members)
            {
                foreach (var candidate in new[] { Token(member.ShortName), Token(member.Name) })
                {
                    if (
                        candidate.Length > 0
                        && (
                            candidate == token
                            || token.Length >= 6 && candidate.StartsWith(token, StringComparison.Ordinal)
                            || candidate.Length >= 6 && token.StartsWith(candidate, StringComparison.Ordinal)
                        )
                    )
                    {
                        return member.Id;
                    }
                }
            }
        }
        return null;
    }

    private static bool TestPath(string file)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();
        var generated =
            new[] { ".designer.cs", ".g.cs", ".g.i.cs", "modelsnapshot.cs", ".pb.go", "_pb2.py" }.Any(suffix =>
                name.EndsWith(suffix, StringComparison.Ordinal)
            )
            || name.Contains(".generated.", StringComparison.Ordinal)
            || file.ToLowerInvariant().Split('/').SkipLast(1).Any(part => part is "migrations" or "bin" or "obj");
        return TaskParsing.IsTestFile(file)
            || generated
            || file.Contains("node_modules/", StringComparison.Ordinal)
            || name.StartsWith("appsettings.", StringComparison.Ordinal)
                && name.Contains("test", StringComparison.Ordinal)
            || name.StartsWith(".env.", StringComparison.Ordinal) && name.Contains("test", StringComparison.Ordinal)
            || file.Split('/')
                .SkipLast(1)
                .Any(part =>
                    new[] { "mock", "example", "sample" }.Any(prefix =>
                        part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    )
                );
    }

    private static string Parentheses(string text, bool inside)
    {
        StringBuilder output = new();
        var depth = 0;
        foreach (var character in text)
        {
            switch (character)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (inside ? depth > 0 : depth is 0)
                    {
                        output.Append(character);
                    }

                    break;
            }
        }

        return output.ToString();
    }
}

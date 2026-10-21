using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture;

public sealed partial class SystemReader
{
    private ArchitectureFinding[] Findings()
    {
        List<ArchitectureFinding> found = [];
        var units = _nodes.Values.Where(node => node.Kind is "service" or "frontend").Select(node =>
        {
            var unit = new BuildUnit { Name = node.Name, Path = node.Id };
            unit.References.AddRange(_links.Values.Where(link => link.From == node.Id && link.Style is "grpc" or "http" && !link.To.StartsWith("ext:", StringComparison.Ordinal)).Select(link => link.To));
            return unit;
        }).ToArray();
        foreach (var cycle in ArchitectureAnalyzer.Analyze(new("", "service", [], units)).Findings.Where(finding => finding.Kind is "cycle"))
        {
            var evidence = _links.Values.Where(link => link.Style is "grpc" or "http" && cycle.Title.Contains(_nodes[link.From].Name, StringComparison.Ordinal) && cycle.Title.Contains(_nodes[link.To].Name, StringComparison.Ordinal) && link.Evidence.Count > 0).Select(link => link.Evidence[0] + " (" + _nodes[link.From].Name + " → " + _nodes[link.To].Name + ")").ToArray();
            found.Add(new() { Kind = "sync-cycle", Level = "problem", Title = cycle.Title.Replace("depend on each other", "call each other synchronously", StringComparison.Ordinal), Why = "A circle of synchronous calls: one slow or failing service holds up the others, and none can be deployed or reasoned about alone. Break it with events or by moving the shared part.", Fix = "Let one direction become asynchronous (an event the other subscribes to), or move what both need so calls go one way.", Evidence = evidence, Key = "sync-" + cycle.Key, Measure = cycle.Measure });
        }
        foreach (var link in _links.Values.Where(link => link.Style is "grpc" && (link.Contracts?.Count >= 8 || link.Uses >= 20)))
            found.Add(new() { Kind = "chatty", Level = "info", Title = _nodes[link.From].Name + " talks a lot to " + _nodes[link.To].Name, Why = $"{link.Contracts?.Count ?? 0} contracts, {link.Uses} places in the code: a fine-grained coupling that often means the boundary between the two is in the wrong place (heuristic).", Fix = "If the data changes rarely (reference data), a local copy kept current by events or a cache saves the calls; otherwise a coarser contract that returns what is used together, or moving the data to the caller.", Evidence = link.Evidence, Project = link.From, Key = ArchitectureAnalyzer.Key("chatty", link.From, link.To), Measure = link.Contracts?.Count ?? 0 });
        foreach (var contract in _contracts.Where(contract => contract.Servers.Count is 0))
            found.Add(new() { Kind = "contract-unserved", Level = "warn", Title = contract.Name + " is served by no member", Why = "Declared in " + _nodes[contract.Owner].Name + " but no member implements it: its clients call something outside the workspace, or nothing.", Fix = "Find who serves it outside the workspace and add that service as a member, or remove the contract and its client if nothing does.", Project = contract.Owner, Evidence = [Place(contract.Owner, contract.Declared)], Key = ArchitectureAnalyzer.Key("contract-unserved", contract.Name) });
        foreach (var group in _contracts.Where(contract => contract.Servers.Count > 0 && contract.Clients.Count is 0).GroupBy(contract => contract.Servers[0], StringComparer.Ordinal))
            found.Add(new() { Kind = "contract-unused", Level = "info", Title = _nodes[group.Key].Name + $" serves {group.Count()} contract(s) no other member uses", Why = string.Join(", ", group.Select(contract => contract.Name)) + ". Their clients may be outside the workspace (a front-end, another team), or they are left over.", Fix = "Confirm who uses them outside the workspace (front-ends, other teams); contracts nobody uses can be retired.", Project = group.Key, Evidence = group.Take(6).Select(contract => Place(contract.Owner, contract.Declared)).ToArray(), Key = ArchitectureAnalyzer.Key("contract-unused", group.Key), Measure = group.Count() });
        foreach (var (database, places) in _databases.Where(pair => pair.Value.Count >= 2))
            found.Add(new() { Kind = "shared-database", Level = "warn", Title = string.Join(" and ", places.Select(place => _nodes[place.Member].Name).Order(StringComparer.Ordinal)) + " share the database " + database, Why = "Services sharing a database are coupled through its schema: a change for one can break the other, and neither owns the data. Matched by the database name their configuration declares.", Fix = "Give one service ownership of the data; the others read it through that service's API or events.", Evidence = places.Select(place => Place(place.Member, place.At)).ToArray(), Key = ArchitectureAnalyzer.Key("shared-database", database), Measure = places.Count });
        return found.OrderBy(finding => ArchitectureAnalyzer.Severity(finding.Level)).Select(finding => ArchitectureAnalyzer.Complete(finding, ArchitectureAnalyzer.Harness("internal/system"))).ToArray();
    }
}

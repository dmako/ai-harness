using AwesomeAssertions;
using Harness.Infrastructure.Architecture.Clones;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class RepeatedCodeReaderTests
{
    [Fact]
    public void SameNamedNestedFunctionsOfOneFileFollowOneTemplate()
    {
        // Two nested functions named label share the id app.js#label, as in harness's own app.js.
        const string App = """
            export function linkView(state, links) {
              const name = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : id; };
              const seen = new Set();
              for (const l of links) { if (!seen.has(l.from)) { seen.add(l.from); } }
              return links.map((l) => name(l.from) + " -> " + name(l.to)).join("\n");
            }

            export async function cycleView(state, m, members) {
              const label = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : m.name(id); };
              const response = await fetch("/api/cycles");
              if (!response.ok) { throw new Error(response.statusText); }
              return { cycle: members.map((x) => label(x.id)), size: members.length };
            }

            export function ownerView(members, owners) {
              const memberName = (id) => { const m = members.find((x) => x.project_id === id); return m ? m.title || m.project : id; };
              let total = 0;
              while (total < owners.length) { total++; }
              return owners.length > 0 ? memberName(owners[0].owner) : "nobody";
            }

            export function nodeView(state, nodes) {
              const label = (n) => { const p = state.projects.find((x) => x.id === n.project_id); return p ? pname(p) : n.project; };
              const sorted = [...nodes].sort((a, b) => a.layer - b.layer);
              try { return sorted.map(label); } catch (error) { console.error(error); return []; }
            }

            export function issueView(state, issues) {
              const name = (id) => { const p = state.projects.find((x) => x.id === id); return p ? pname(p) : id; };
              switch (issues.length) {
                case 0: return "none";
                default: return issues.map((i) => `${name(i.project)}: ${i.title}`).join(", ");
              }
            }
            """;
        using var tree = new ReaderTree(new() { ["web/app.js"] = App });

        var findings = new RepeatedCodeReader(tree.Repository()).Read();

        findings
            .Should()
            .Contain(finding =>
                finding.Kind == "template" && finding.Key.Contains("web/app.js#label|web/app.js#label")
            );
    }
}

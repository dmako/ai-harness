using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Xunit;

namespace Harness.Integration.Memory;

public sealed class MemoryOracleTests
{
    [Fact]
    public async Task AllGoSimpleLowercaseMappingsKeepLegacyHashesCompatible()
    {
        var mapping = await Run(Oracle(), "lower", "unused");
        List<string> mismatches = [];
        foreach (var entry in mapping.AsObject())
        {
            // Punctuation/space normalization is not relevant to these cased letters.
            if (MemoryText.Normalize(entry.Key) != entry.Value!.GetValue<string>()) mismatches.Add(entry.Key);
        }
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task UnicodeWordRangeBoundariesMatchGoSimilarityTokenization()
    {
        var unicode = await Run(Oracle(), "unicode", "unused");
        var ranges = unicode["word_ranges"]!.AsArray().Select(node => (First: node![0]!.GetValue<int>(), Last: node[1]!.GetValue<int>())).ToArray();
        var scalars = ranges.SelectMany(range => new[] { range.First, range.Last, range.First - 1, range.Last + 1 }).Where(Rune.IsValid).Distinct();
        foreach (var scalar in scalars)
        {
            var isWord = ranges.Any(range => scalar >= range.First && scalar <= range.Last);
            Assert.Equal(isWord ? 0 : 1, MemoryText.Similarity("abc" + new Rune(scalar) + "def", "abc def"));
        }
    }

    [Fact]
    public async Task RedactionMatchesGoIncludingAsciiBoundariesAndWhitespace()
    {
        var fixtures = await Run(Oracle(), "redaction", "unused");
        foreach (var fixture in fixtures.AsObject())
        {
            var expected = fixture.Value!.GetValue<string>();
            Assert.Equal(expected, CredentialRedactor.Redact(fixture.Key));
            if (expected != fixture.Key)
                Assert.Throws<ArgumentException>(() => MemoryText.Validate(new MemoryInput { Type = "fact", Content = fixture.Key }));
        }
    }

    [Fact]
    public async Task GoDatabaseRoundtripsThroughCSharpWithIdenticalSearchHistoryAndAudit()
    {
        var oracle = Oracle();
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "legacy.db");
        var expected = await Run(oracle, "seed", path);
        JsonNode after;
        using (var database = HarnessDatabase.Open(path, cancellationToken: Token))
        {
            var store = new MemoryStore(database);
            Equal(expected, Report(store));
            Assert.Equal("duplicate", store.Put(new MemoryInput { Type = "fact", Content = "İSTANBUL 𐐀 Á Ž Σ K Unicode compatibility" }, Token).Status);
            var old = store.Search(new MemoryQuery { Text = "TOML" }, Token)[0];
            store.Supersede(old.Id, new MemoryInput { Content = "Use TOML with explicit source metadata for config.", Source = new MemorySource("agent", "C# review", "codex"), SupersedeReason = "C# roundtrip" }, Token);
            store.Put(new MemoryInput { Type = "fact", Content = "žluťoučký memory 🧪 survives C# → Go", Source = new MemorySource("import", "roundtrip", "csharp") }, Token);
            var retired = store.Search(new MemoryQuery { Text = "Subsystem" }, Token)[0];
            store.Retire(retired.Id, "human:csharp", "roundtrip retirement", Token);
            after = Report(store);
        }

        Equal(after, await Run(oracle, "read", path));
        using var reopened = HarnessDatabase.OpenReadOnly(path);
        Equal(after, Report(new MemoryStore(reopened)));
        Assert.Empty(DatabaseBackups.List(path));
    }

    [Fact]
    public async Task LayersAgreeWithGoForConflictsExpiryBranchTextAndLimits()
    {
        var oracle = Oracle();
        using var directory = new TemporaryDirectory();
        var expected = await Run(oracle, "layers", directory.Path);
        using var project = HarnessDatabase.OpenReadOnly(Path.Combine(directory.Path, "project.db"));
        using var workspace = HarnessDatabase.OpenReadOnly(Path.Combine(directory.Path, "workspace.db"));
        using var user = HarnessDatabase.OpenReadOnly(Path.Combine(directory.Path, "user.db"));
        var layers = new MemoryLayers(new MemoryStore(project),
        [
            new MemoryLevel(MemoryScope.Parse("workspace:home"), new MemoryStore(workspace)),
            new MemoryLevel(MemoryScope.User, new MemoryStore(user)),
        ]);
        var target = MemoryScope.Parse("path:src/api/handler.cs");
        var queries = new MemoryQuery[]
        {
            new(), new() { Scope = target, Branch = "main" }, new() { IncludeInactive = true },
            new() { Text = "SQLite" }, new() { Scope = target, Limit = 1 },
        };
        var results = queries.Select(query => layers.Search(query, Token)).ToArray();
        Assert.All(results, result => Assert.Empty(result.Errors));
        var actual = JsonSerializer.SerializeToNode(results.Select(result => result.Memories.Select(Snapshot)))!;
        Equal(expected, actual);
    }

    private static JsonNode Report(MemoryStore store)
    {
        var path = MemoryScope.Parse("path:src/api/handler.cs");
        var queries = new MemoryQuery[]
        {
            new(), new() { Scope = path, Branch = "feature", Limit = 3 }, new() { Scope = path, Branch = "main" },
            new() { Text = "migration storage" }, new() { Text = "migration", Scope = path }, new() { Text = "!!!" },
            new() { Types = ["decision", "warning"], IncludeInactive = true, Limit = 100 }, new() { Text = "\"; DROP TABLE memories; --" },
        };
        var all = store.Search(new MemoryQuery { IncludeInactive = true, Limit = 100 }, Token);
        return JsonSerializer.SerializeToNode(new
        {
            all = all.Select(Snapshot),
            searches = queries.Select(query => store.Search(query, Token).Select(Snapshot)),
            histories = all.ToDictionary(memory => memory.Id, memory => store.History(memory.Id, Token).Select(Snapshot), StringComparer.Ordinal),
            events = all.ToDictionary(memory => memory.Id, memory => store.Events(memory.Id, Token).Select(item => new
            {
                @event = item.Event, related_id = item.RelatedId, actor = item.Actor, detail = item.Detail, at = item.At.ToString(),
            }), StringComparer.Ordinal),
            similar = store.Similar("decision", "Store project memory in the SQLite project database", Token).Select(item => new { memory = Snapshot(item.Memory), similarity = item.Similarity }),
        })!;
    }

    private static object Snapshot(MemoryRecord memory) => new
    {
        id = memory.Id, type = memory.Type, content = memory.Content, scope = memory.Scope.ToString(),
        source_type = memory.Source.Type, source_ref = memory.Source.Ref, recorded_by = memory.Source.By,
        confidence = memory.Confidence, superseded_by = memory.SupersededBy, supersede_reason = memory.SupersedeReason,
        valid_from = memory.ValidFrom?.ToString(), valid_until = memory.ValidUntil?.ToString(),
        created_at = memory.CreatedAt.ToString(), updated_at = memory.UpdatedAt.ToString(),
        overrides = memory.Overrides.Count is 0 ? null : memory.Overrides,
    };

    private static void Equal(JsonNode expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), "Memory oracle mismatch. Expected: " + expected.ToJsonString() + " Actual: " + actual.ToJsonString());

    private static string Oracle()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_GO_MEMORY_ORACLE");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Skip("Set HARNESS_GO_MEMORY_ORACLE to the bridge built by Memory/Go/build-oracle.ps1.");
        Assert.True(File.Exists(executable));
        return executable;
    }

    private static async Task<JsonNode> Run(string oracle, string operation, string path)
    {
        var start = new ProcessStartInfo(oracle)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(Token);
            var error = process.StandardError.ReadToEndAsync(Token);
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(process.ExitCode is 0, await error);
            return JsonNode.Parse(await output)!;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-memory-oracle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

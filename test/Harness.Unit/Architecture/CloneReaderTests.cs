using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Infrastructure.Architecture.Clones;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class CloneReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> SourceCases()
    {
        var document = JsonNode.Parse(
            File.ReadAllText(Path.Combine(Root(), "test/fixtures/compatibility/clones/source-cases.json"))
        )!;
        foreach (var node in document["cases"]!.AsArray())
        {
            var name = node!["name"]!.GetValue<string>();
            var files = node["files"]!
                .AsObject()
                .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
            yield return [name, files, "forms"];
            yield return [name, files, "read"];
            yield return [name, files, "findings"];
        }
        const string Raw = """"
            class First
            {
                public string Format(int id)
                {
                    var doc = """{"active":true}""";
                    var pattern = @"a""b";
                    var value = id + 12;
                    if (value > 0) { return doc + pattern + value.ToString(); }
                    return "";
                }
            }
            """";
        var csharp = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/First.cs"] = Raw,
            ["src/Second.cs"] = Raw.Replace("First", "Second", StringComparison.Ordinal)
                .Replace("id", "key", StringComparison.Ordinal),
            ["src/Generated.g.cs"] = Raw,
            ["test/FirstTests.cs"] = Raw,
        };
        foreach (var mode in new[] { "forms", "read", "findings" })
        {
            yield return ["CSharpRawStringsAndGeneratedFiltering", csharp, mode];
        }

        var javascript = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web/a.ts"] =
                "export function load(value: string): string { const match = /a+[b]/g; const inner = (x) => { if (match.test(x)) { return value + x; } return ''; }; return inner(value); }",
            ["web/b.ts"] =
                "export function load(text: string): string { const match = /a+[b]/g; const inner = (x) => { if (match.test(x)) { return text + x; } return ''; }; return inner(text); }",
        };
        foreach (var mode in new[] { "forms", "read", "findings" })
        {
            yield return ["TypeScriptClosuresAndRegex", javascript, mode];
        }
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task CapturedSourceFixturesMatchFrozenGo(string name, Dictionary<string, string> files, string mode)
    {
        using var tree = new ReaderTree(files);
        var extractor = new CloneExtractor(tree.Repository());
        var functions = extractor.Extract();
        object actual;
        if (mode is "findings")
        {
            actual = new RepeatedCodeReader(tree.Repository()).Read();
        }
        else if (mode is "forms")
        {
            actual = functions
                .Select(function => new
                {
                    function,
                    canonical = function.Canonical,
                    shape = function.Shape,
                    signature = function.Signature,
                    statements = function.Statements.Select(statement => new
                    {
                        key = statement.Key,
                        own = statement.Own,
                        raw = statement.Raw,
                        line = statement.Line,
                        size = statement.Size,
                        idiom = statement.Idiom,
                        header = statement.Header,
                        work = statement.Work,
                        callee = statement.Callee,
                        data = statement.Data,
                    }),
                })
                .ToArray();
        }
        else
        {
            var branches = extractor.Branches();
            var copies = CloneComparison.Find(functions);
            var templates = CloneComparison.Templates(functions);
            actual = new
            {
                functions,
                branches,
                copies,
                templates,
                sequences = CloneSequences.Find(functions, copies, templates),
                contracts = CloneContracts.Find(functions, branches),
            };
        }
        var expected = await OracleAsync(
            new
            {
                mode,
                root = tree.Root,
                files = tree.Files,
            }
        );
        var node = JsonSerializer.SerializeToNode(actual);
        if (!JsonNode.DeepEquals(node, expected))
        {
            var directory = Path.Combine(Root(), "artifacts/clones-differential");
            Directory.CreateDirectory(directory);
            var prefix = name.Replace(':', '-') + "-" + mode;
            await File.WriteAllTextAsync(
                Path.Combine(directory, prefix + "-actual.json"),
                node?.ToJsonString(new() { WriteIndented = true }),
                Token
            );
            await File.WriteAllTextAsync(
                Path.Combine(directory, prefix + "-expected.json"),
                expected?.ToJsonString(new() { WriteIndented = true }),
                Token
            );
        }
        JsonNode.DeepEquals(node, expected).Should().BeTrue(name + " " + mode + ": " + Difference(node, expected, "$"));
    }

    private static string Difference(JsonNode? actual, JsonNode? expected, string path)
    {
        if (actual is JsonObject a && expected is JsonObject b)
        {
            foreach (var key in a.Select(pair => pair.Key).Union(b.Select(pair => pair.Key), StringComparer.Ordinal))
            {
                if (!JsonNode.DeepEquals(a[key], b[key]))
                {
                    return Difference(a[key], b[key], path + "." + key);
                }
            }
        }

        if (actual is JsonArray aa && expected is JsonArray bb)
        {
            if (aa.Count != bb.Count)
            {
                return path + $": {aa.Count} items versus {bb.Count}";
            }

            for (var index = 0; index < aa.Count; index++)
            {
                if (!JsonNode.DeepEquals(aa[index], bb[index]))
                {
                    return Difference(aa[index], bb[index], path + "[" + index + "]");
                }
            }
        }
        return path + ": " + actual?.ToJsonString() + " versus " + expected?.ToJsonString();
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Harness fixture root");
    }

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Explicit frozen oracle path runs without a shell."
    )]
    internal static async Task<JsonNode?> OracleAsync(object input)
    {
        var oracle =
            Environment.GetEnvironmentVariable("HARNESS_GO_CLONES_ORACLE")
            ?? throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_CLONES_ORACLE using Clones/build_oracle.py.");
        using var process = new Process
        {
            StartInfo = new(oracle)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await error);
            return JsonNode.Parse(await output);
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
}

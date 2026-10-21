using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Xunit;

namespace Harness.Integration.Providers;

public sealed class GenericGoContractTests
{
    [Theory]
    [InlineData("symbols")]
    [InlineData("wildcards")]
    [InlineData("pattern")]
    [InlineData("chain")]
    [InlineData("overview")]
    [InlineData("files")]
    [InlineData("architecture")]
    [InlineData("miss")]
    [InlineData("mapping_error")]
    public async Task GenericMappingResultAndRenderedArgumentsMatchPinnedGo(string scenario)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_PROVIDER_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_PROVIDER_ORACLE to the bridge built by Providers/build_oracle.py.");
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ProviderFixture();
        var capability = scenario switch
        {
            "chain" => Capabilities.PackageTypes,
            "overview" => Capabilities.Overview,
            "files" => Capabilities.FileSearch,
            "architecture" => Capabilities.ArchDependencies,
            _ => Capabilities.SymbolSearch,
        };
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"query":"{query}","name":"{query_name}","pattern":"{query_regex}","limit":"{limit}","more":"{limit_more}","budget":"{budget}","target":"{target_name}","root":"{root}","path":"{path}","package":"{package}","version":"{version}","type":"{type}","unknown":"{unknown}","exact":false,"note":"q={query}"}""")!;
        var mapping = scenario switch
        {
            "symbols" => new CapabilityMapping { Tool = "answer", Args = args, Items = "hits", Fields = new(StringComparer.Ordinal) { ["name"] = "name", ["path"] = "path", ["start_line"] = "line", ["score"] = "rank" } },
            "wildcards" => new() { Tool = "answer", Args = args, Items = "files.*.lines", Where = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"match":true,"count":1}""")!, Fields = new(StringComparer.Ordinal) { ["path"] = "$key", ["name"] = "name" } },
            "pattern" => new() { Tool = "answer", Args = args, Pattern = @"(?m)^(?P<path>[^:]+):(?P<start_line>\d+):(?P<name>[^\n]+)$", Truncated = "more results" },
            "chain" => new() { Tool = "answer", Args = args, Fields = new(StringComparer.Ordinal) { ["name"] = "name" }, Combine = "all", Chain = [new() { Tool = "second", Fields = new(StringComparer.Ordinal) { ["name"] = "name" } }] },
            "architecture" => new() { Tool = "answer", Args = args, Fields = new(StringComparer.Ordinal) { ["name"] = "0", ["path"] = "1", ["related_name"] = "2", ["related_path"] = "3", ["members"] = "4", ["confidence"] = "5", ["count"] = "6" } },
            "miss" => new() { Tool = "answer", Args = args, Miss = "not found" },
            "mapping_error" => new() { Tool = "answer", Args = args, Error = "message" },
            _ => new() { Tool = "answer", Args = args },
        };
        var body = scenario switch
        {
            "symbols" => JsonSerializer.Serialize(new { hits = new object[] { new { name = "A", path = Path.Combine(fixture.Root, "src", "A.cs"), line = "7", rank = "0.9" }, new { name = "B", path = "src/B.cs", line = "8", rank = "0.8" }, new { name = "C", path = "src/C.cs", line = "9", rank = "0.7" } } }),
            "wildcards" => """{"files":{"src/B.cs":{"lines":[{"name":"B","match":true,"count":1},{"name":"context","match":false,"count":1}]},"src/A.cs":{"lines":[{"name":"A","match":true,"count":1}]}}}""",
            "pattern" => "src/A.cs:3:Alpha&lt;T&gt;\nsrc/B.cs:4:Beta\nmore results",
            "chain" => """[{"name":"Class"}]""",
            "overview" => """{"modules":2,"entry":"Program.cs"}""",
            "files" => """["src/A.cs","src/B.cs"]""",
            "architecture" => """[["A","src/A.cs","B","src/B.cs",["A","B"],"0.9",8]]""",
            "miss" => "symbol not found",
            _ => """{"message":"not indexed"}""",
        };
        fixture.Write("answer.json", body);
        if (scenario is "chain")
            fixture.Write("second.json", """[{"name":"Interface"}]""");
        var configuration = new ProviderConfiguration { Type = "mcp", Command = "dotnet", Args = [ProviderFixture.Worker, "normal", fixture.Root], TimeoutMs = 5000, Capabilities = new(StringComparer.Ordinal) { [capability] = mapping } };
        var request = new ProviderRequest(capability, "Service.A+(x) # 🧪", Limit: 2, Target: "Target.Last", Budget: 1024);
        await using var candidate = GenericMcpProvider.Build("fakeidx", configuration, fixture.Root, new ProviderProcessFactory());
        var result = await candidate.ExecuteAsync(request, token);
        var rendered = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "answer.args"), token);
        var input = Path.Combine(fixture.Root, "oracle-input");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { Root = fixture.Root, Name = "fakeidx", Configuration = configuration, Request = request }), token);
        var start = new ProcessStartInfo(oracle) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(input);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        process.ExitCode.Should().Be(0, await stderr);
        using var expected = JsonDocument.Parse(await stdout);
        var actual = JsonSerializer.SerializeToElement(new { capabilities = candidate.Capabilities(), items = result.Items, summary = result.Summary, text = result.Text, truncated = result.Truncated, bytes = result.Bytes, error = result.Error?.Message ?? "" });
        JsonElement.DeepEquals(expected.RootElement, actual).Should().BeTrue($"Go: {expected.RootElement}\nC#: {actual}");
        using var renderedExpected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "answer.args"), token));
        using var renderedActual = JsonDocument.Parse(rendered);
        JsonElement.DeepEquals(renderedExpected.RootElement, renderedActual.RootElement).Should().BeTrue($"Go args: {renderedExpected.RootElement}\nC# args: {renderedActual.RootElement}");
    }
}

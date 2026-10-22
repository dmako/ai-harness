using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Mcp.Providers;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class ProtocolGoContractTests
{
    [Theory]
    [InlineData("call")]
    [InlineData("structured")]
    [InlineData("unsupported")]
    [InlineData("incompatible")]
    [InlineData("tool_error")]
    [InlineData("search_project")]
    [InlineData("search_debug")]
    [InlineData("project_overview")]
    [InlineData("provider_status")]
    [InlineData("package_api")]
    [InlineData("package_missing")]
    public async Task ProtocolAndPublicFacadeMatchPinnedGo(string scenario)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_PROTOCOL_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_GO_PROTOCOL_ORACLE to Providers/protocol_oracle.go built with build_oracle.py --bridge."
            );
        }

        var token = TestContext.Current.CancellationToken;
        using var fixture = new ProviderFixture();
        fixture.Write(
            "harness_provider_info.json",
            scenario is "incompatible"
                ? """{"protocol":"harness-provider/2","name":"old"}"""
                : ProtocolProviderTests.Info
        );
        fixture.Write("code_symbol_search.json", ProtocolProviderTests.Answer);
        fixture.Write("package_info.json", """{"items":[],"text":"public class Result {}"}""");
        if (scenario is "structured")
        {
            fixture.Write(
                "code_symbol_search.result.json",
                """{"content":[{"type":"text","text":"ignored"}],"structuredContent":{"items":[{"kind":"symbol","name":"Structured","provider":"spoofed","tool":"find"}]}}"""
            );
        }

        if (scenario is "tool_error")
        {
            fixture.Write("code_symbol_search.json", """{"error":"corrupt index","hint":"rebuild"}""");
            fixture.Write("code_symbol_search.error", "");
        }
        var request = new ProviderRequest(
            scenario is "unsupported" ? Capabilities.TextSearch : Capabilities.SymbolSearch,
            "žluťoučký 🧪",
            "src",
            1,
            "Target",
            1024
        );
        var tool = scenario switch
        {
            "search_debug" => "search_project",
            "package_missing" => "package_api",
            "search_project" or "project_overview" or "provider_status" or "package_api" => scenario,
            _ => "",
        };
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            tool switch
            {
                "search_project" => scenario is "search_debug"
                    ? """{"query":"Alpha","kind":"symbol","debug":true,"limit":1}"""
                    : """{"query":"Alpha","kind":"symbol","limit":1}""",
                "package_api" => scenario is "package_missing"
                    ? """{"action":"types","package":"Foo"}"""
                    : """{"action":"info","package":"Foo","version":"1.2"}""",
                _ => """{}""",
            }
        )!;
        JsonElement actual;
        await using (var provider = ProtocolProviderTests.Create(fixture))
        {
            if (tool.Length > 0)
            {
                await using var registry = new ProviderRegistry();
                registry.Register(provider);
                actual = JsonSerializer.SerializeToElement(
                    await new ProviderTools(registry, new Snapshot(fixture.Root)).CallAsync(tool, arguments, token)
                );
            }
            else
            {
                var capabilities = provider.Capabilities();
                var result = await provider.ExecuteAsync(request, token);
                actual = JsonSerializer.SerializeToElement(
                    new
                    {
                        capabilities,
                        items = result.Items,
                        summary = result.Summary,
                        text = result.Text,
                        truncated = result.Truncated,
                        bytes = result.Bytes,
                        error = result.Error?.Message ?? "",
                    }
                );
            }
        }
        var input = Path.Combine(fixture.Root, "oracle-input");
        await File.WriteAllTextAsync(
            input,
            JsonSerializer.Serialize(
                new
                {
                    Root = fixture.Root,
                    Name = "analyzer",
                    Configuration = ProtocolProviderTests.Configuration(fixture),
                    Request = request,
                    Tool = tool,
                    Arguments = arguments,
                }
            ),
            token
        );
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
        process.StartInfo.ArgumentList.Add(input);
        process.Start().Should().BeTrue();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        process.ExitCode.Should().Be(0, await stderr);
        var expected = JsonNode.Parse(await stdout)!;
        var candidate = JsonNode.Parse(actual.GetRawText())!;
        Normalize(expected);
        Normalize(candidate);
        JsonNode.DeepEquals(expected, candidate).Should().BeTrue($"Go: {expected}\nC#: {candidate}");
    }

    private static void Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (key is "latency_ms")
                {
                    obj[key] = 0;
                    continue;
                }
                if (key is "detail" && value is JsonValue text && text.TryGetValue<string>(out var detail))
                {
                    obj[key] = Regex.Replace(
                        detail,
                        @"pid \d+",
                        "pid <PID>",
                        RegexOptions.NonBacktracking,
                        TimeSpan.FromSeconds(1)
                    );
                }
                else if (value is not null)
                {
                    Normalize(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                if (value is not null)
                {
                    Normalize(value);
                }
            }
        }
    }

    private sealed class Snapshot(string root) : IProviderProjectSession
    {
        public Task<ProviderProjectSnapshot> ReadProviderProjectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderProjectSnapshot(root));

        public Task<IReadOnlyDictionary<string, JsonElement>> ReadProviderStatisticsAsync(
            string provider,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(new Dictionary<string, JsonElement>());
    }
}

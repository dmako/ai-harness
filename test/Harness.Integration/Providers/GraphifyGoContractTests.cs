using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Providers.Graphify;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class GraphifyGoContractTests
{
    [Theory]
    [InlineData("symbols")]
    [InlineData("symbols_limit")]
    [InlineData("callers")]
    [InlineData("references")]
    [InlineData("path")]
    [InlineData("impact")]
    [InlineData("type_impact")]
    [InlineData("error")]
    [InlineData("unsupported")]
    public async Task ResultsMatchTheFrozenGoGraphifyAdapter(string scenario)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_GRAPHIFY_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_GO_GRAPHIFY_ORACLE using Graphify/build_oracle.py; the fake CLI oracle uses the current platform worker."
            );
        }

        var token = TestContext.Current.CancellationToken;
        using var fixture = new GraphifyFixture();
        fixture.Ready("borrowed");
        if (scenario is "type_impact")
        {
            fixture.Write(
                "symbols.json",
                """{"success":true,"items":[{"id":"iface","label":"N.IService","declaration_kind":"interface"}]}"""
            );
        }

        if (scenario is "error")
        {
            fixture.Write(
                "symbols.json",
                """{"success":false,"error":{"code":"invalid_query","message":"fixture failure"},"items":[]}"""
            );
        }

        if (scenario is "impact")
        {
            fixture.Write(
                "callers-controller.json",
                """{"success":true,"items":[{"relation":"calls","location":{"file":"src/Api.cs","line":7},"origin":{"id":"api","label":"N.Api.Execute()","declaration_kind":"method"}}]}"""
            );
        }

        var capability = scenario switch
        {
            "symbols" or "symbols_limit" or "error" => Capabilities.SymbolSearch,
            "references" => Capabilities.References,
            "impact" or "type_impact" => Capabilities.Impact,
            "unsupported" => Capabilities.TextSearch,
            _ => Capabilities.Callers,
        };
        var query = scenario switch
        {
            "type_impact" => "IService",
            "symbols" or "symbols_limit" or "error" => "Save",
            _ => "IService.Save",
        };
        var request = new ProviderRequest(
            capability,
            query,
            scenario is "path" ? "elsewhere" : "",
            scenario is "symbols_limit" ? 1 : 20
        );
        await using var manager = fixture.Manager();
        await using var provider = new GraphifyProvider(fixture.Options(), manager);
        var result = await provider.ExecuteAsync(request, token);
        var actual = JsonSerializer.SerializeToElement(
            new
            {
                capabilities = provider.Capabilities(),
                items = result.Items,
                truncated = result.Truncated,
                bytes = result.Bytes,
                error = result.Error?.Message ?? "",
            }
        );
        var command = Path.ChangeExtension(ProviderFixture.Worker, ".exe");
        if (!OperatingSystem.IsWindows())
        {
            command = Path.Combine(fixture.DirectoryRoot, "graphify-worker");
            await File.WriteAllTextAsync(
                command,
                "#!/bin/sh\nexec dotnet \"$HARNESS_GRAPHIFY_TEST_WORKER\" \"$@\"\n",
                token
            );
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var input = Path.Combine(fixture.DirectoryRoot, "oracle-input.json");
        await File.WriteAllTextAsync(
            input,
            JsonSerializer.Serialize(
                new
                {
                    Root = fixture.Root,
                    Command = command,
                    Request = request,
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
        process.StartInfo.Environment["HARNESS_FAKE_GRAPHIFY"] = fixture.Data;
        process.StartInfo.Environment["HARNESS_GRAPHIFY_TEST_WORKER"] = ProviderFixture.Worker;
        process.StartInfo.Environment["HARNESS_CONFIG_DIR"] = Path.GetDirectoryName(fixture.RecordsPath);
        process.Start().Should().BeTrue();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        process.ExitCode.Should().Be(0, await error);
        using var expected = JsonDocument.Parse(await output);
        JsonElement
            .DeepEquals(expected.RootElement, actual)
            .Should()
            .BeTrue($"Go: {expected.RootElement}\nC#: {actual}");
    }
}

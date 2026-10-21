using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Graphify;
using Harness.Core.Providers;
using Harness.Infrastructure.Graphify;
using Harness.Providers.Graphify;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class GraphifyRealTests
{
    [Fact]
    public async Task RealGraphifyAnswersCompilerCallersReferencesAndImpactAndStopsOnlyItsOwnSession()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_REAL_GRAPHIFY");
        if (string.IsNullOrWhiteSpace(executable))
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_REAL_GRAPHIFY to an installed graphify-csharp executable for the isolated compiler smoke test.");
        var token = TestContext.Current.CancellationToken;
        using var fixture = new GraphifyFixture();
        var project = Path.Combine(fixture.Root, "Sample.csproj");
        File.Delete(Path.Combine(fixture.Root, "App.slnx"));
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Code.cs"), """
            namespace Smoke;
            public interface IService { void Save(); }
            public sealed class Service : IService { public void Save() { } }
            public sealed class Controller(IService service) { public void Run() => service.Save(); }
            public sealed class Api(Controller controller) { public void Execute() => controller.Run(); }
            """, token);
        using (var restore = new Process { StartInfo = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } })
        {
            foreach (var argument in new[] { "restore", project, "-bl:" + Path.Combine(ProviderFixture.RepositoryRoot, "artifacts", "graphify-real-restore-" + Guid.NewGuid().ToString("N") + ".binlog") })
                restore.StartInfo.ArgumentList.Add(argument);
            restore.Start().Should().BeTrue();
            var output = restore.StandardOutput.ReadToEndAsync(token);
            var error = restore.StandardError.ReadToEndAsync(token);
            try
            { await restore.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(45), token); }
            finally { if (!restore.HasExited) restore.Kill(entireProcessTree: true); }
            restore.ExitCode.Should().Be(0, (await output) + (await error));
        }
        var options = new GraphifyOptions(fixture.Root, executable) { RecordsPath = fixture.RecordsPath, StopOnExit = true, ReadyWait = TimeSpan.FromSeconds(60), StartedBy = "isolated harness smoke" };
        await using var manager = new GraphifySessionManager(options);
        var provider = new GraphifyProvider(options, manager);
        int pid = 0;
        string? id = null;
        try
        {
            var callers = await provider.ExecuteAsync(new(Capabilities.Callers, "IService.Save"), token);
            callers.Error.Should().BeNull();
            callers.Items.Should().Contain(item => item.Name == "Run");
            var references = await provider.ExecuteAsync(new(Capabilities.References, "IService.Save"), token);
            references.Error.Should().BeNull();
            references.Items.Should().NotBeEmpty();
            var impact = await provider.ExecuteAsync(new(Capabilities.Impact, "IService.Save"), token);
            impact.Error.Should().BeNull();
            impact.Items.Should().Contain(item => item.Name == "Execute" && item.Depth == 2);
            var session = (await manager.ListAsync(false, token)).Single(session => session.Root == manager.Root && session.Reachable);
            session.Ready.Should().BeTrue();
            pid = session.Pid;
            id = session.Id;
            File.Exists(Path.Combine(manager.SessionDirectory, id + ".json")).Should().BeTrue("cleanup must use graphify's actual descriptor directory");
            if (Environment.GetEnvironmentVariable("HARNESS_GRAPHIFY_SMOKE_RECEIPT") is { Length: > 0 } receipt)
                await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { executable_version = FileVersionInfo.GetVersionInfo(executable).ProductVersion, ready = session.Ready, callers = callers.Items.Select(item => item.Name), references = references.Items.Count, impact = impact.Items.Select(item => new { item.Name, item.Depth }), owned_session = session.StartedBy == "isolated harness smoke" }), token);
        }
        finally
        {
            await provider.DisposeAsync();
            // A failed assertion must still release the isolated compiler session.
            foreach (var session in (await manager.ListAsync(false, CancellationToken.None)).Where(session => session.Root == manager.Root && session.Reachable))
                await manager.StopAsync(session.Id, CancellationToken.None);
        }
        if (pid > 0)
            await ProviderFixture.GoneAsync(pid);
        (await manager.ListAsync(false, token)).Should().NotContain(session => session.Id == id);
        File.Exists(Path.Combine(manager.SessionDirectory, id + ".json")).Should().BeFalse();
    }
}

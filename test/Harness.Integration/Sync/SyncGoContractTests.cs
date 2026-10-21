using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Sync;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Xunit;

namespace Harness.Integration.Sync;

[Collection("Provider processes")]
public sealed class SyncGoContractTests
{
    [Fact]
    public async Task FrozenGoAndCSharpAgreeAfterEachEditRevertCommitRenameAndBranchTransition()
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_SYNC_ORACLE");
        if (string.IsNullOrWhiteSpace(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_SYNC_ORACLE using Sync/build_oracle.py.");
        }

        using var fixture = await SyncFixture.CreateAsync();
        var oracleDatabase = Path.Combine(fixture.DirectoryRoot, "oracle.db");
        var first = await CompareAsync();
        fixture.Write("a.cs", "dirty edit");
        await CompareAsync();
        fixture.Write("a.cs", "a");
        await CompareAsync();
        fixture.Write("b.cs", "committed edit");
        await fixture.CommitAsync("edit");
        await CompareAsync();
        File.Move(Path.Combine(fixture.Root, "a.cs"), Path.Combine(fixture.Root, "renamed.cs"));
        File.Delete(Path.Combine(fixture.Root, "b.cs"));
        fixture.Write("new.cs", "new");
        fixture.Write("gen/x.cs", "ignored");
        await CompareAsync();
        await fixture.CommitAsync("moves");
        await CompareAsync();
        await CompareAsync(new() { Mode = "full" });
        await CompareAsync(new() { Mode = "since", Since = first.Head });
        await fixture.GitAsync("switch", "-c", "topic");
        fixture.Write("topic.cs", "topic");
        await fixture.CommitAsync("topic");
        await CompareAsync();
        await fixture.GitAsync("switch", "main");
        await CompareAsync();
        await fixture.GitAsync("checkout", "--detach");
        await CompareAsync();
        await CompareAsync();
        // The driver database is also readable by the C# storage layer without migration changes.
        using var go = HarnessDatabase.OpenReadOnly(oracleDatabase);
        go.SchemaVersion().Should().Be(19);

        async Task<SyncRun> CompareAsync(SyncOptions? options = null)
        {
            options = (options ?? new()) with { Ignore = ["gen/"] };
            var result = (await fixture.RunAsync(options)).Files!;
            using var process = new Process { StartInfo = new(oracle) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(fixture.DirectoryRoot, "absent.gitconfig");
            process.Start();
            var token = TestContext.Current.CancellationToken;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { fixture.Root, Database = oracleDatabase, options.Mode, options.Since, options.Ignore }).AsMemory(), token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
                process.ExitCode.Should().Be(0, await error);
                var expected = JsonNode.Parse(await output);
                var state = result.State!;
                var actual = JsonSerializer.SerializeToNode(new
                {
                    mode = result.Mode,
                    reason = result.Reason,
                    @base = result.Base,
                    head = result.Head,
                    branch_switch = result.BranchSwitch,
                    changes = result.Changes.Select(change => new { kind = change.Kind, path = change.Path, old_path = change.OldPath }),
                    files = new { added = result.Files.Added, changed = result.Files.Changed, removed = result.Files.Removed, unchanged = result.Files.Unchanged, hashed = result.Files.Hashed },
                    state = new { branch = state.Branch, commit = state.Commit, detached = state.Detached, dirty = state.Dirty, dirty_paths = state.DirtyPaths, pending = state.Pending, providers_stale = state.ProvidersStale },
                    hashes = fixture.Hashes(),
                });
                JsonNode.DeepEquals(actual, expected).Should().BeTrue("Go: {0}\nC#: {1}", expected, actual);
                return result;
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
    }
}

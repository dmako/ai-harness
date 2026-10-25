using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Architecture.Metrics;

/// <summary>Measures .NET projects and writes a Microsoft.CodeAnalysis.Metrics XML report.</summary>
public interface IMetricsTool
{
    string Version { get; }

    /// <summary>Projects (absolute paths) in dependency order, lowest first; returns the report's path.</summary>
    Task<string> MeasureAsync(string root, IReadOnlyList<string> projects, string report, CancellationToken token);
}

/// <summary>Why a run failed: sdk, restore, load or platform, each with its own fix.</summary>
public sealed class MetricsToolException : Exception
{
    public MetricsToolException()
        : this("worker", "the metrics tool failed") { }

    public MetricsToolException(string message)
        : this("worker", message) { }

    public MetricsToolException(string message, Exception innerException)
        : base(message, innerException) => Failure = "worker";

    public MetricsToolException(string failure, string message)
        : base(message) => Failure = failure;

    public string Failure { get; }
}

/// <summary>
/// Metrics.exe from Microsoft.CodeAnalysis.Metrics, restored once into a harness-owned cache with the build host
/// from Microsoft.CodeAnalysis.Workspaces.MSBuild, which the package omits (TASK-0040). The analysed repository is
/// never changed. .NET Framework 4.7.2: Windows only.
/// </summary>
public sealed class MetricsExecutable(string cache) : IMetricsTool
{
    private const string MetricsVersion = "5.6.0";
    private const string BuildHostVersion = "4.12.0";

    public string Version => $"Microsoft.CodeAnalysis.Metrics {MetricsVersion}";

    private string Home => Path.Combine(cache, "metrics-" + MetricsVersion);

    public async Task<string> MeasureAsync(
        string root,
        IReadOnlyList<string> projects,
        string report,
        CancellationToken token
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new MetricsToolException("platform", "Microsoft.CodeAnalysis.Metrics runs on Windows only");
        }

        var executable = await EnsureAsync(token).ConfigureAwait(false);
        File.Delete(report);
        var (code, output) = await RunAsync(
                executable,
                [.. projects.Select(project => "/project:" + project), "/out:" + report, "/quiet"],
                root,
                token
            )
            .ConfigureAwait(false);
        return code is 0 && File.Exists(report)
            ? report
            : throw new MetricsToolException("load", "Metrics.exe could not measure the projects: " + Tail(output));
    }

    private async Task<string> EnsureAsync(CancellationToken token)
    {
        var tool = Path.Combine(Home, "Metrics");
        var executable = Path.Combine(tool, "Metrics.exe");
        var ready = Path.Combine(Home, "ready");
        if (File.Exists(ready) && File.Exists(executable))
        {
            return executable;
        }

        // PackageDownload fetches the packages through the user's NuGet sources without building anything. The
        // SDK's own framework needs no other package, and inherited package source mappings would hide these two.
        var restore = Path.Combine(Home, "restore");
        var packages = Path.Combine(Home, "packages");
        StorageFiles.CreateDirectory(restore);
        await File.WriteAllTextAsync(
                Path.Combine(restore, "nuget.config"),
                """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """,
                token
            )
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(restore, "restore.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>$(BundledNETCoreAppTargetFramework)</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageDownload Include="Microsoft.CodeAnalysis.Metrics" Version="[{MetricsVersion}]" />
                    <PackageDownload Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="[{BuildHostVersion}]" />
                  </ItemGroup>
                </Project>
                """,
                token
            )
            .ConfigureAwait(false);
        var (code, output) = await RunAsync(
                "dotnet",
                ["restore", "restore.csproj", "--packages", packages, "--nologo"],
                restore,
                token
            )
            .ConfigureAwait(false);
        var metrics = Path.Combine(packages, "microsoft.codeanalysis.metrics", MetricsVersion, "Metrics");
        var host = Path.Combine(
            packages,
            "microsoft.codeanalysis.workspaces.msbuild",
            BuildHostVersion,
            "contentFiles",
            "any",
            "any"
        );
        if (code is not 0 || !Directory.Exists(metrics) || !Directory.Exists(host))
        {
            throw new MetricsToolException(
                "restore",
                "could not restore Microsoft.CodeAnalysis.Metrics (offline, or a NuGet source refused it): "
                    + Tail(output)
            );
        }

        Copy(metrics, tool);
        foreach (var folder in new[] { "BuildHost-net472", "BuildHost-netcore" })
        {
            Copy(Path.Combine(host, folder), Path.Combine(tool, folder));
        }

        await File.WriteAllTextAsync(ready, Version + "\n", token).ConfigureAwait(false);
        return executable;
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            StorageFiles.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string directory,
        CancellationToken token
    )
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new MetricsToolException("sdk", executable + " did not start");
        }
        catch (Win32Exception error)
        {
            throw new MetricsToolException(
                "sdk",
                $"{executable} could not start ({error.Message}): install the .NET SDK and put dotnet on PATH"
            );
        }

        using (process)
        {
            var output = new StringBuilder();
            var reading = Task.WhenAll(Pump(process.StandardOutput, output), Pump(process.StandardError, output));
            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await reading.ConfigureAwait(false);
            return (process.ExitCode, output.ToString());
        }
    }

    private static async Task Pump(StreamReader reader, StringBuilder output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }
    }

    private static string Tail(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(4)
            .ToArray();
        return lines.Length is 0 ? "no output" : string.Join(" | ", lines);
    }
}

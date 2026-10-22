using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Harness.Cli;

internal sealed record BuildInformation(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("modified")] bool Modified,
    [property: JsonPropertyName("go_version")] string Runtime,
    [property: JsonPropertyName("platform")] string Platform
)
{
    internal static BuildInformation Current()
    {
        var assembly = typeof(BuildInformation).Assembly;
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        var version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "dev";
        var os = (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()) switch
        {
            (true, _) => "windows",
            (_, true) => "darwin",
            _ => "linux",
        };
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Unsupported process architecture."),
        };
        return new BuildInformation(
            version,
            metadata.GetValueOrDefault("CommitHash") ?? string.Empty,
            metadata.GetValueOrDefault("BuildTimestamp") ?? string.Empty,
            bool.TryParse(metadata.GetValueOrDefault("Modified"), out var modified) && modified,
            RuntimeInformation.FrameworkDescription,
            $"{os}/{architecture}"
        );
    }

    public override string ToString()
    {
        var commit = Commit.Length switch
        {
            0 => "unknown",
            > 12 => Commit[..12],
            _ => Commit,
        };
        return $"harness {Version} (commit {commit}{(Modified ? "-dirty" : string.Empty)}, built {(Date.Length == 0 ? "unknown" : Date)}, {Runtime}, {Platform})";
    }
}

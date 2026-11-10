using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Harness.Integration;

internal static class TestDirectory
{
    /// <summary>
    /// The target framework folder of the build output (for example net11.0), taken from this test assembly,
    /// which is built with the same TargetFramework as the projects whose binaries the tests start.
    /// </summary>
    public static string TargetFramework { get; } =
        "net"
        + new FrameworkName(
            AppContext.TargetFrameworkName
                ?? throw new InvalidOperationException("The test host has no target framework name.")
        ).Version.ToString(2);

    [SuppressMessage(
        "Major Code Smell",
        "S2925:Thread.Sleep should not be used in tests",
        Justification = "IDisposable cleanup briefly yields while exiting child processes release or stop writing files."
    )]
    public static void Delete(string directory)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                // MCP transport shutdown can return before descendant working-directory handles are released (Windows),
                // and a child process that is still exiting can add files mid-delete ("Directory not empty" elsewhere).
                Thread.Sleep(50);
            }
        }
    }
}

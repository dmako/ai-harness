using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Harness.Integration;

internal static class TestDirectory
{
    [SuppressMessage(
        "Major Code Smell",
        "S2925:Thread.Sleep should not be used in tests",
        Justification = "IDisposable cleanup briefly yields while Windows releases terminated child-process file handles."
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
            catch (IOException)
                when (OperatingSystem.IsWindows() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                // MCP transport shutdown can return before descendant working-directory handles are released.
                Thread.Sleep(50);
            }
        }
    }
}

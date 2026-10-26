using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Harness.Integration;

internal static class TestDirectory
{
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

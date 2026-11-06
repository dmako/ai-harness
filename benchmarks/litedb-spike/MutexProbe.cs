using System.Diagnostics;

namespace LiteDbSpike;

/// <summary>
/// LiteDB's shared mode serializes processes with a named Mutex ("Global\" + a hash of the file path). This holds
/// one and asks a second process to take it: if the child gets it at once, the mutex does not cross processes.
/// </summary>
internal static class MutexProbe
{
    public static int Run()
    {
        var name = @"Global\litedb-spike-" + Environment.ProcessId;
        using var mutex = new Mutex(false, name);
        mutex.WaitOne();
        using var child = Process.Start(
            new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "mutex-child", name },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        )!;
        var answer = child.StandardOutput.ReadToEnd().Trim();
        child.WaitForExit();
        mutex.ReleaseMutex();
        var shared = answer is "blocked";
        Console.WriteLine(
            $"named mutex held here; another process {(shared ? "could not take it: it crosses processes" : "took it at once: it does NOT cross processes")} "
                + $"({Runtime.Mode}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription})"
        );
        return shared ? 0 : 1;
    }

    public static int Child(string name)
    {
        using var mutex = new Mutex(false, name);
        var taken = mutex.WaitOne(TimeSpan.FromMilliseconds(500));
        Console.WriteLine(taken ? "taken" : "blocked");
        if (taken)
        {
            mutex.ReleaseMutex();
        }

        return 0;
    }
}

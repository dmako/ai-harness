using System.Diagnostics;
using System.Text;

namespace Harness.Infrastructure.Clients;

public static class SetupQuery
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Queries installed Git and shells with fixed arguments and no user command text.")]
    public static async Task<string> QueryAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false, true) } };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        var started = false;
        try
        {
            process.Start();
            started = true;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await stderr;
            return process.ExitCode is 0 ? (await stdout).Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "";
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}

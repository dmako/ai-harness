using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Infrastructure.Projects;

namespace Harness.Cli.Ui;

/// <summary>
/// Health's Fix buttons: a check's fix, run for the user. Only a harness subcommand of <see cref="Allowed"/> runs, as
/// this binary with plain arguments and no shell; every other fix is the user's to do.
/// </summary>
internal static partial class HarnessFix
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "install",
        "instructions",
        "hooks install",
        "sync",
    };

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    /// <summary>An argument a fix may pass: words, flags, ids, paths and lists.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_./:=,-]+\z")]
    private static partial Regex Token { get; }

    /// <summary>A command quoted in a sentence: run 'harness sync'.</summary>
    [GeneratedRegex(@"'(harness [^']+)'")]
    private static partial Regex Quoted { get; }

    /// <summary>
    /// A fix's harness arguments ("harness install (or see …)", "run 'harness sync'"), or null when it is not one
    /// harness runs for the user.
    /// </summary>
    internal static IReadOnlyList<string>? Command(string fix)
    {
        // a fix that starts with the command is that command; one in a sentence is quoted in it
        var command =
            !fix.StartsWith("harness ", StringComparison.Ordinal) && Quoted.Match(fix) is { Success: true } quoted
                ? quoted.Groups[1].Value
                : fix;
        foreach (var cut in new[] { " (", " — ", "; " })
        {
            var at = command.IndexOf(cut, StringComparison.Ordinal);
            command = at < 0 ? command : command[..at];
        }

        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words is not ["harness", var verb, .. var rest])
        {
            return null;
        }

        var sub = rest is [var next, ..] && !next.StartsWith('-') ? verb + " " + next : verb;
        return Allowed.Contains(sub) && words.Skip(1).All(Token.IsMatch) ? words[1..] : null;
    }

    internal static bool Fixable(HealthCheck check) =>
        check.Level is not "ok" && check.Fix is { Length: > 0 } fix && Command(fix) is not null;

    /// <summary>Runs this binary with the arguments in the project for two minutes at most; what it said.</summary>
    internal static async Task<JsonObject> RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken token
    )
    {
        var message = "harness " + string.Join(' ', arguments);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate harness");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "harness.dll"));
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(Limit);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start harness");
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return Result(false, message + " took longer than two minutes and was stopped", "");
            }

            var text = ((await output) + (await errors)).Trim();
            return process.ExitCode is 0
                ? Result(true, message + " done", text)
                : Result(false, $"{message} failed (exit {process.ExitCode})", text);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return Result(false, message + " failed: " + error.Message, "");
        }
    }

    private static JsonObject Result(bool ok, string message, string output) =>
        new()
        {
            ["ok"] = ok,
            ["message"] = message,
            ["output"] = output.Length > 2000 ? output[^2000..] : output,
        };
}

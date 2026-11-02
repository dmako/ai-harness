using System.ComponentModel;
using System.Diagnostics;
using Harness.Core.Providers;

namespace Harness.Infrastructure.Processes;

public sealed class ProviderProcessFactory : IProviderProcessFactory
{
    public IProviderProcess Start(ProviderProcessOptions options)
    {
        var (executable, directory, environment) = Prepare(options);
        return OperatingSystem.IsWindows()
            ? WindowsProviderProcess.Start(
                executable,
                options.Arguments,
                directory,
                environment,
                options.AllowDetachedChildren
            )
            : UnixProviderProcess.Start(executable, options.Arguments, directory, environment);
    }

    internal static (string Executable, string Directory, Dictionary<string, string> Environment) Prepare(
        ProviderProcessOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Command);
        if (
            options.Command.Contains('\0', StringComparison.Ordinal)
            || options.Arguments.Any(argument => argument.Contains('\0', StringComparison.Ordinal))
        )
        {
            throw new ArgumentException("Process arguments must not contain NUL.", nameof(options));
        }

        var environment = System
            .Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string)entry.Value!,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
            );
        foreach (var (key, value) in options.Environment)
        {
            if (
                key.Length is 0
                || key.Contains('=', StringComparison.Ordinal)
                || key.Contains('\0', StringComparison.Ordinal)
                || value?.Contains('\0', StringComparison.Ordinal) is true
            )
            {
                throw new ArgumentException("Invalid process environment entry.", nameof(options));
            }

            if (value is null)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = value;
            }
        }
        var directory = options.WorkingDirectory.Length is 0
            ? System.Environment.CurrentDirectory
            : Path.GetFullPath(options.WorkingDirectory);
        var executable = ResolveExecutable(options.Command, environment);
        return (executable, directory, environment);
    }

    private static string ResolveExecutable(string command, Dictionary<string, string> environment)
    {
        var extensions = Extensions(command, environment);
        if (
            command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
        )
        {
            var full = Path.GetFullPath(command);
            return extensions.Select(extension => full + extension).FirstOrDefault(File.Exists) ?? full;
        }

        foreach (
            var directory in environment
                .GetValueOrDefault("PATH", "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new Win32Exception(2, "Provider executable was not found on PATH: " + command);
    }

    /// <summary>
    /// The extensions a command is looked up with. Windows finds a bare name as its shell does, by PATHEXT, limited
    /// to what it can start (.com, .exe, and .bat and .cmd through cmd.exe, as dnx.cmd and npx.cmd are), never as
    /// an extensionless file: the one beside npx.cmd is a POSIX shell script. A name with such an extension is taken
    /// as written.
    /// </summary>
    internal static string[] Extensions(string command, IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [""];
        }

        string[] startable = [".COM", ".EXE", ".BAT", ".CMD"];
        if (startable.Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase))
        {
            return [""];
        }

        var listed = environment
            .GetValueOrDefault("PATHEXT", "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => startable.Contains(extension, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return listed.Length is 0 ? startable : listed;
    }
}

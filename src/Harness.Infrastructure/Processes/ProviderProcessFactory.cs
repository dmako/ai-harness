using System.ComponentModel;
using System.Diagnostics;
using Harness.Core.Providers;

namespace Harness.Infrastructure.Processes;

public sealed class ProviderProcessFactory : IProviderProcessFactory
{
    public IProviderProcess Start(ProviderProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Command);
        if (options.Command.Contains('\0', StringComparison.Ordinal) || options.Arguments.Any(argument => argument.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException("Process arguments must not contain NUL.", nameof(options));
        var environment = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in options.Environment)
        {
            if (key.Length is 0 || key.Contains('=', StringComparison.Ordinal) || key.Contains('\0', StringComparison.Ordinal) || value?.Contains('\0', StringComparison.Ordinal) is true)
                throw new ArgumentException("Invalid process environment entry.", nameof(options));
            if (value is null)
                environment.Remove(key);
            else
                environment[key] = value;
        }
        var directory = options.WorkingDirectory.Length is 0 ? System.Environment.CurrentDirectory : Path.GetFullPath(options.WorkingDirectory);
        var executable = ResolveExecutable(options.Command, environment);
        return OperatingSystem.IsWindows()
            ? WindowsProviderProcess.Start(executable, options.Arguments, directory, environment)
            : UnixProviderProcess.Start(executable, options.Arguments, directory, environment);
    }

    private static string ResolveExecutable(string command, Dictionary<string, string> environment)
    {
        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return Path.GetFullPath(command);
        var extensions = OperatingSystem.IsWindows() ? new[] { "", ".exe", ".com" } : [""];
        foreach (var directory in environment.GetValueOrDefault("PATH", "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        throw new Win32Exception(2, "Provider executable was not found on PATH: " + command);
    }
}

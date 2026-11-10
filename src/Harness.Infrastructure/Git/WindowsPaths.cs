using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Harness.Infrastructure.Git;

/// <summary>Windows path spellings the managed API keeps as typed: 8.3 short names (RUNNER~1) become long names.</summary>
internal static class WindowsPaths
{
    /// <summary>The path with its short names expanded; the path itself when it has none or cannot be read.</summary>
    internal static string LongPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~', StringComparison.Ordinal))
        {
            return path;
        }

        var buffer = new char[Math.Max(path.Length * 2, 260)];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = Native.GetLongPathNameW(path, buffer, (uint)buffer.Length);
            if (length is 0)
            {
                return path;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            buffer = new char[length];
        }

        return path;
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute to generate P/Invoke marshalling code at compile time",
        Justification = "The repository disallows unsafe code; this bounded Windows ABI declaration uses runtime marshalling."
    )]
    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint size);
    }
}

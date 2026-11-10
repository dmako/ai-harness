namespace Harness.Core.Projects;

/// <summary>
/// Whether two paths name the same file as the file system sees it: ignoring case on Windows, exactly elsewhere.
/// Paths a client hands harness keep the casing it typed (VS Code opens c:\ for C:\), so paths compared with paths
/// from another source (a provider's index, a stored record) must go through this.
/// </summary>
public static class PathIdentity
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Compares the two paths in full form (relative parts resolved, separators normalized).</summary>
    public static bool Same(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), Comparison);
}

using System.Text;

namespace Harness.Infrastructure.Workspaces;

// Go orders UTF-8 strings by bytes; UTF-16 ordinal order differs for supplementary runes.
internal sealed class WorkspaceNameComparer : IComparer<string>
{
    internal static WorkspaceNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y) =>
        Encoding.UTF8.GetBytes(x ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? ""));
}

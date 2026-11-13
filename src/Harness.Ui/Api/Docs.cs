namespace Harness.Ui.Api;

// The docs area: GET /api/docs and /api/docs/{slug} (UiAssets): the guide and reference and their sections. The
// list's document entries use capitalised keys (Name, File, Title); names match case-insensitively.

/// <summary>GET /api/docs.</summary>
public sealed class DocList
{
    public required IReadOnlyList<DocInfo> Docs { get; init; }
    public required IReadOnlyList<DocSection> Sections { get; init; }
}

/// <summary>A document harness ui serves (guide.md, reference.md).</summary>
public sealed class DocInfo
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string Title { get; init; }
}

/// <summary>A section of a document, addressed by its slug.</summary>
public sealed class DocSection
{
    public required string Doc { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
}

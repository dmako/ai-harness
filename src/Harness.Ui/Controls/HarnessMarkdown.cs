using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering;

namespace Harness.Ui.Controls;

/// <summary>Markdown rendered with the application's theme and Mermaid support.</summary>
public class HarnessMarkdown : MarkdownViewer
{
    public HarnessMarkdown()
    {
        this.UseMermaid();
        LinkClicked += OnLinkClicked;
    }

    protected override Type StyleKeyOverride => typeof(MarkdownViewer);

    private void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (e.Url.StartsWith("harness-doc:", StringComparison.Ordinal))
        {
            e.Handled = true;
            var slug = Uri.UnescapeDataString(e.Url["harness-doc:".Length..]);
            RaiseEvent(new NavigateEventArgs(AppLink.NavigateEvent, "#/docs/" + Uri.EscapeDataString(slug)));
            return;
        }
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            e.Handled = true;
        }
    }
}

/// <summary>Documentation uses hash links to identify other documentation sections.</summary>
public sealed class DocsMarkdown : HarnessMarkdown
{
    private static readonly MarkdownPipeline DocsPipeline = CreatePipeline();

    public DocsMarkdown() => Pipeline = DocsPipeline;

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseGitHubExtensions();
        builder.DocumentProcessed += document =>
        {
            foreach (var link in document.Descendants<LinkInline>())
            {
                if (!link.IsImage && link.Url is { } url && url.StartsWith('#', StringComparison.Ordinal))
                {
                    link.Url = "harness-doc:" + Uri.EscapeDataString(url[1..]);
                }
            }
        };
        return builder.Build();
    }
}

using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Harness.Ui.Controls;

/// <summary>
/// The docs' Markdown as controls, never as HTML (app.js markdown()): headings, paragraphs, lists, tables, fenced
/// code, inline code, bold and links. Text is selectable. A link to "#slug" goes to that docs section in place, an
/// http(s) link opens outside, any other is plain text. Written here rather than taken from a package: the docs use
/// this small subset, and a parser of our own has no reflection, so trimming and AOT stay clean (TASK-0159).
/// </summary>
public sealed partial class MarkdownBlock : StackPanel
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Source));

    public MarkdownBlock() => Spacing = 10;

    /// <summary>The Markdown shown.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            Children.Clear();
            Children.AddRange(Render(Source ?? ""));
        }
    }

    /// <summary>The blocks of <paramref name="source"/>, in order.</summary>
    public static IReadOnlyList<Control> Render(string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<Control>();
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }
                i++;
                blocks.Add(Code(string.Join("\n", code)));
            }
            else if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                blocks.Add(Heading(heading.Groups[1].Value.Length, heading.Groups[2].Value));
                i++;
            }
            else if (ItemPattern().IsMatch(line))
            {
                var ordered = OrderedPattern().IsMatch(line);
                var items = new List<string>();
                while (i < lines.Length && (ItemPattern().IsMatch(lines[i]) || (ContinuationPattern().IsMatch(lines[i]) && items.Count > 0)))
                {
                    if (ItemPattern().IsMatch(lines[i]))
                    {
                        items.Add(ItemPattern().Replace(lines[i], "", 1));
                    }
                    else
                    {
                        items[^1] += " " + lines[i].Trim();
                    }
                    i++;
                }
                blocks.Add(List(items, ordered));
            }
            else if (line.StartsWith('|', StringComparison.Ordinal))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].StartsWith('|', StringComparison.Ordinal))
                {
                    rows.Add(lines[i++]);
                }
                blocks.Add(Table(rows));
            }
            else if (line.Trim().Length is 0)
            {
                i++;
            }
            else
            {
                var paragraph = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0 && !BlockStart().IsMatch(lines[i]))
                {
                    paragraph.Add(lines[i++]);
                }
                blocks.Add(Text(string.Join(" ", paragraph)));
            }
        }
        return blocks;
    }

    private static SelectableTextBlock Text(string text, string? classes = null)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (classes is not null)
        {
            block.Classes.Add(classes);
        }
        block.Inlines!.AddRange(Inlines(text));
        return block;
    }

    /// <summary># is the section's own title (h2), ## below it h3, and so on (app.js: level n is h(n+1)).</summary>
    private static SelectableTextBlock Heading(int level, string text)
    {
        var block = Text(text, level <= 1 ? "h2" : "h3");
        block.Margin = new Thickness(0, level <= 2 ? 8 : 4, 0, 0);
        if (level >= 3)
        {
            block.FontSize = 15;
        }
        return block;
    }

    private static Border Code(string code) =>
        new Border
        {
            Classes = { "inset" },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock { Text = code, Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap },
            },
        };

    private static StackPanel List(List<string> items, bool ordered)
    {
        var list = new StackPanel { Spacing = 4 };
        for (var n = 0; n < items.Count; n++)
        {
            var row = new DockPanel();
            var mark = new TextBlock
            {
                Text = ordered ? $"{n + 1}." : "•",
                Width = 22,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(Text(items[n]));
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>A table: the first row its head; the separator row (|---|) left out; it scrolls sideways when wide.</summary>
    private static ScrollViewer Table(IReadOnlyList<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var body = rows.Where((row, k) => k is not 1 || !SeparatorPattern().IsMatch(row)).Select(Cells).ToArray();
        var columns = body.Max(r => r.Length);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columns))) };
        for (var r = 0; r < body.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < body[r].Length; c++)
            {
                var cell = Text(body[r][c]);
                cell.MaxWidth = 420;
                if (r is 0)
                {
                    cell.FontWeight = FontWeight.SemiBold;
                }
                var frame = new Border
                {
                    Child = cell,
                    Padding = new Thickness(8, 4),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Classes = { "tcell" },
                };
                Grid.SetRow(frame, r);
                Grid.SetColumn(frame, c);
                grid.Children.Add(frame);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    /// <summary>A line's inline code, bold and links as runs and link controls.</summary>
    private static IEnumerable<Inline> Inlines(string text)
    {
        var last = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > last)
            {
                yield return new Run(text[last..m.Index]);
            }
            if (m.Groups[1].Success)
            {
                var code = new Run(m.Groups[1].Value[1..^1]);
                code.Classes.Add("code");
                yield return code;
            }
            else if (m.Groups[2].Success)
            {
                yield return new Run(m.Groups[2].Value[2..^2]) { FontWeight = FontWeight.SemiBold };
            }
            else
            {
                yield return Link(m.Groups[3].Value, m.Groups[4].Value);
            }
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            yield return new Run(text[last..]);
        }
    }

    private static Inline Link(string text, string url)
    {
        if (url.StartsWith('#', StringComparison.Ordinal))
        {
            return new InlineUIContainer(
                new AppLink { Classes = { "link", "inline" }, Href = "#/docs/" + Uri.EscapeDataString(url[1..]), Content = text }
            );
        }
        if (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal))
        {
            return new InlineUIContainer(
                new HyperlinkButton { Classes = { "inline" }, NavigateUri = new Uri(url), Content = text, Padding = new Thickness(0) }
            );
        }
        return new Run(text);
    }

    [GeneratedRegex(@"^(#{1,5}) (.*)$", RegexOptions.None, 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*([-*]|\d+\.) ", RegexOptions.None, 1000)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"^\s*\d+\. ", RegexOptions.None, 1000)]
    private static partial Regex OrderedPattern();

    [GeneratedRegex(@"^\s{2,}\S", RegexOptions.None, 1000)]
    private static partial Regex ContinuationPattern();

    [GeneratedRegex(@"^\|[\s:|-]+\|?$", RegexOptions.None, 1000)]
    private static partial Regex SeparatorPattern();

    [GeneratedRegex(@"^(#{1,5} |```|\||\s*([-*]|\d+\.) )", RegexOptions.None, 1000)]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"(`[^`]+`)|(\*\*[^*]+\*\*)|\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.None, 1000)]
    private static partial Regex InlinePattern();
}

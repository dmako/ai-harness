using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Harness.Web;
using Xunit;

namespace Harness.Unit;

/// <summary>
/// The design system's rules for harness ui, as the Go harness checks them (internal/brand/brand_test.go): colours
/// come from the palette, fonts, radii and text sizes from the design system's tokens, and spacing is in rem so the
/// layout grows with the reader's text size.
/// </summary>
public sealed partial class DesignSystemTests
{
    private static readonly string[] Interfaces = ["app.js", "style.css", "index.html"];

    [GeneratedRegex(@"#[0-9a-fA-F]{3}(?:[0-9a-fA-F]{3})?(?:[0-9a-fA-F]{2})?\b|\brgba?\(|\bhsla?\(")]
    private static partial Regex ColourLiteral { get; }

    [GeneratedRegex(
        @"font(?:-family)?\s*:[^;}""]*?(?:sans-serif|monospace|\bserif\b|system-ui|Menlo|Helvetica|Arial|Inter\b|Segoe|SFMono|Consolas)"
    )]
    private static partial Regex FontFamily { get; }

    [GeneratedRegex(@"border-radius\s*:\s*([^;}""]+)")]
    private static partial Regex Radius { get; }

    [GeneratedRegex(@"font-size\s*:\s*([0-9.]+)(px|rem|em|pt)")]
    private static partial Regex FontSize { get; }

    [GeneratedRegex(
        @"(?:^|[\s;{""])(?:padding|margin|gap|row-gap|column-gap|min-height|max-width|grid-template-columns|flex-basis)(?:-[a-z]+)?\s*:\s*([^;}""]+)"
    )]
    private static partial Regex Spacing { get; }

    [GeneratedRegex(@"\b([0-9.]+)px\b")]
    private static partial Regex PixelValue { get; }

    private static string Asset(string name)
    {
        using var stream =
            typeof(UiRoutes).Assembly.GetManifestResourceStream("Harness.Web.Assets/" + name)
            ?? throw new InvalidOperationException(name + " is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IEnumerable<(string File, int Line, string Text)> Lines() =>
        Interfaces.SelectMany(file => Asset(file).Split('\n').Select((text, i) => (file, i + 1, text)));

    [Fact]
    public void InterfacesOnlyReferenceThePalette()
    {
        var literals = Lines()
            .Where(l => ColourLiteral.IsMatch(l.Text) && !l.Text.Contains("&#", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: colour literal {ColourLiteral.Match(l.Text).Value}")
            .ToArray();
        literals.Should().BeEmpty("colours are defined once, in the palette, and referenced");
        Asset("brand.css").Should().Contain("--brand-teal: #006b6c");
    }

    [Fact]
    public void InterfacesUseTheDesignSystemsFontsRadiiTextSizesAndRemSpacing()
    {
        List<string> faults = [];
        foreach (var (file, line, text) in Lines())
        {
            var at = $"{file}:{line}";
            if (FontFamily.Match(text) is { Success: true } font)
            {
                faults.Add($"{at}: font family {font.Value}: use var(--font) or var(--mono)");
            }

            // a line saying "px on purpose" is a drawn shape
            if (!text.Contains("px on purpose", StringComparison.Ordinal))
            {
                foreach (Match spacing in Spacing.Matches(text))
                {
                    if (PixelValue.Matches(spacing.Groups[1].Value).Any(value => value.Groups[1].Value is not "0"))
                    {
                        faults.Add($"{at}: spacing {spacing.Groups[1].Value.Trim()} in px: use rem or a --space token");
                    }
                }
            }

            // only the tiny sizes drawn inside marks and charts (10.5px at most) are written out
            foreach (Match size in FontSize.Matches(text))
            {
                if (
                    size.Groups[2].Value is not "px"
                    || double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) > 10.5
                )
                {
                    faults.Add($"{at}: font size {size.Value}: use a --text token");
                }
            }

            foreach (Match radius in Radius.Matches(text))
            {
                foreach (var value in radius.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (value is not "0" && !value.StartsWith("var(--radius-", StringComparison.Ordinal))
                    {
                        faults.Add($"{at}: radius {value}: use a --radius token");
                    }
                }
            }
        }

        faults.Should().BeEmpty();
    }

    [Fact]
    public void DesignSystemDefinesItsTokensComponentsAndDarkTheme()
    {
        var css = Asset("brand.css");
        string[] tokens =
        [
            "--page:",
            "--surface:",
            "--ink:",
            "--accent:",
            "--critical:",
            "--font:",
            "--mono:",
            "--radius-md:",
            "--space-4:",
            "--shadow-2:",
            "--text-xs:",
            "--text-xl:",
            ".primary",
            ".highlight",
            ".card",
            ".pill",
            ".badge",
            ".table",
            ":root[data-theme=\"dark\"]",
            "prefers-color-scheme: dark",
        ];
        tokens.Where(token => !css.Contains(token, StringComparison.Ordinal)).Should().BeEmpty();
    }
}

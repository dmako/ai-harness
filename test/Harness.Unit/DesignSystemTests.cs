using AwesomeAssertions;
using Harness.Web;
using Xunit;

namespace Harness.Unit;

/// <summary>
/// The embedded design system defines the shared brand tokens and components.
/// </summary>
public sealed class DesignSystemTests
{
    private static string Asset(string name)
    {
        using var stream =
            typeof(UiRoutes).Assembly.GetManifestResourceStream("Harness.Web.Assets/" + name)
            ?? throw new InvalidOperationException(name + " is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

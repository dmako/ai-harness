using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Harness.Web;

public sealed record UiDocument(string Name, string File, string Title);

public sealed record UiSection(string Doc, string Slug, string Title, string? Body = null);

[SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "The documentation URLs use lowercase ASCII slugs."
)]
public static partial class UiAssets
{
    public static IReadOnlyList<UiDocument> Documents { get; } =
    [new("guide", "guide.md", "Using harness"), new("reference", "reference.md", "Reference")];
    public static IReadOnlyList<UiSection> Sections { get; } = ReadSections();

    public static byte[] Read(string name)
    {
        using var stream =
            typeof(UiAssets).Assembly.GetManifestResourceStream("Harness.Web." + name)
            ?? throw new FileNotFoundException("unknown embedded asset");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    internal static void Map(WebApplication app)
    {
        foreach (
            var (path, name, type, cache) in new[]
            {
                ("/", "index.html", "text/html; charset=utf-8", false),
                ("/index.html", "index.html", "text/html; charset=utf-8", false),
                ("/app.js", "app.js", "text/javascript; charset=utf-8", false),
                ("/style.css", "style.css", "text/css; charset=utf-8", false),
                ("/brand.css", "brand.css", "text/css; charset=utf-8", false),
                ("/logo.svg", "logo.svg", "image/svg+xml", true),
                ("/favicon.png", "logo.png", "image/png", true),
                ("/apple-touch-icon.png", "logo.png", "image/png", true),
            }
        )
        {
            var content = Read("Assets/" + name);
            app.MapMethods(
                path,
                ["GET", "HEAD"],
                (HttpContext context) =>
                {
                    if (cache)
                    {
                        context.Response.Headers.CacheControl = "public, max-age=86400";
                    }
                    return Results.Bytes(content, type);
                }
            );
        }
        app.MapMethods(
            "/api/docs",
            ["GET", "HEAD"],
            () =>
                WireJson(
                    new JsonObjectMap
                    {
                        ["docs"] = Documents.Select(d => new Dictionary<string, string>
                        {
                            ["Name"] = d.Name,
                            ["File"] = d.File,
                            ["Title"] = d.Title,
                        }),
                        ["sections"] = Sections.Select(s => new JsonObjectMap
                        {
                            ["doc"] = s.Doc,
                            ["slug"] = s.Slug,
                            ["title"] = s.Title,
                        }),
                    }
                )
        );
        app.MapMethods(
            "/api/docs/{slug}",
            ["GET", "HEAD"],
            (string slug) =>
                Find(slug) is { } section
                    ? WireJson(
                        new JsonObjectMap
                        {
                            ["doc"] = section.Doc,
                            ["slug"] = section.Slug,
                            ["title"] = section.Title,
                            ["body"] = section.Body,
                        }
                    )
                    : WireJson(new JsonObjectMap { ["error"] = "no such section" }, statusCode: 400)
        );
    }

    private static IResult WireJson(object value, int statusCode = 200) =>
        Results.Text(HarnessJson.Serialize(value), "application/json; charset=utf-8", statusCode: statusCode);

    public static UiSection? Find(string query)
    {
        query = query.Trim().TrimStart('#').ToLowerInvariant();
        return Sections.FirstOrDefault(s => s.Slug == query || s.Doc == query && s.Slug == s.Doc)
            ?? Sections.FirstOrDefault(s =>
                s.Slug.Contains(Slug(query), StringComparison.Ordinal)
                || s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static string Slug(string title) =>
        SlugCharacters.Replace(title.Trim().ToLowerInvariant(), "").Replace(' ', '-');

    [GeneratedRegex("[^a-z0-9 -]", RegexOptions.NonBacktracking)]
    private static partial Regex SlugCharacters { get; }

    private static List<UiSection> ReadSections()
    {
        List<UiSection> result = [];
        foreach (var document in Documents)
        {
            var current = new UiSection(document.Name, document.Name, document.Title);
            List<string> body = [];
            var fence = false;
            void Flush()
            {
                var text = string.Join('\n', body).Trim();
                if (text.Length > 0 || current.Slug != document.Name)
                {
                    result.Add(current with { Body = text });
                }
                body.Clear();
            }
            foreach (var line in Encoding.UTF8.GetString(Read("Documentation/" + document.File)).Split('\n'))
            {
                if (line.Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    fence = !fence;
                }
                if (!fence && line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    var title = line[3..].Trim().Replace("`", "", StringComparison.Ordinal);
                    current = new(document.Name, (document.Name is "guide" ? "guide-" : "") + Slug(title), title);
                }
                body.Add(line);
            }
            Flush();
        }
        return result;
    }
}

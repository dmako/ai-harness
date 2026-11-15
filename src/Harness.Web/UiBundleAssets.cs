using System.IO.Compression;
using System.Text.RegularExpressions;
using Harness.Core.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Harness.Web;

/// <summary>The published Avalonia browser bundle, installed in the ui directory beside harness.</summary>
public static partial class UiBundleAssets
{
    public const string DirectoryName = "ui";
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; frame-ancestors 'none'";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".dat"] = "application/octet-stream",
        [".pdb"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".ttf"] = "font/ttf",
        [".woff2"] = "font/woff2",
    };

    internal static void Map(WebApplication app, string root)
    {
        var directory = Path.GetFullPath(root);
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext context) => Serve(context, directory, "index.html"));
        app.MapMethods("/{**path}", ["GET", "HEAD"], (HttpContext context, string? path) =>
            Serve(context, directory, path is { Length: > 0 } value ? value : "index.html"));
    }

    private static IResult Serve(HttpContext context, string root, string path)
    {
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        var file = Path.GetFullPath(Path.Combine(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || ContentTypes.GetValueOrDefault(Path.GetExtension(file)) is not { } type)
        {
            throw new UiRequestException(404, "404 page not found");
        }
        var compressed = file + ".br";
        var hasRaw = File.Exists(file);
        var hasCompressed = File.Exists(compressed);
        if (!hasRaw && !hasCompressed)
        {
            throw new UiRequestException(path is "index.html" ? 503 : 404,
                path is "index.html" ? "UI bundle missing beside the harness executable" : "404 page not found");
        }

        context.Response.Headers.CacheControl = path.StartsWith("_framework/", StringComparison.Ordinal)
            && Fingerprinted.IsMatch(Path.GetFileName(path))
                ? "public, max-age=31536000, immutable" : "no-cache";
        context.Response.Headers.Vary = "Accept-Encoding";
        var useBrotli = hasCompressed && AcceptsBrotli(context.Request.Headers.AcceptEncoding);
        var stored = useBrotli || !hasRaw ? compressed : file;
        var info = new FileInfo(stored);
        var tag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}-{(useBrotli ? "br" : "raw")}\"");
        if (useBrotli)
        {
            context.Response.Headers.ContentEncoding = "br";
            return Results.File(stored, type, lastModified: info.LastWriteTimeUtc, entityTag: tag);
        }
        if (hasRaw)
        {
            return Results.File(stored, type, lastModified: info.LastWriteTimeUtc, entityTag: tag);
        }
        // Release archives keep only the Brotli copy when one exists. Clients without br get a streamed decode.
        return Results.Stream(async output =>
        {
            await using var input = File.OpenRead(stored);
            await using var decoder = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            await decoder.CopyToAsync(output, context.RequestAborted);
        }, type, lastModified: info.LastWriteTimeUtc, entityTag: tag);
    }

    private static bool AcceptsBrotli(Microsoft.Extensions.Primitives.StringValues headers)
    {
        double? brotli = null;
        double? wildcard = null;
        foreach (var part in headers.ToString().Split(','))
        {
            if (!System.Net.Http.Headers.StringWithQualityHeaderValue.TryParse(part.Trim(), out var value))
            {
                continue;
            }
            if (value.Value.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                brotli = value.Quality ?? 1;
            }
            else if (value.Value is "*")
            {
                wildcard = value.Quality ?? 1;
            }
        }
        return (brotli ?? wildcard ?? 0) > 0;
    }

    [GeneratedRegex(@"\.[a-z0-9]{10}\.(?:wasm|js|mjs|dll|dat)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Fingerprinted { get; }
}

using Harness.Core.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Harness.Web;

/// <summary>
/// The Avalonia harness ui (TASK-0144 spike) at /next/, beside the current UI: served from the browser host's
/// publish output (its wwwroot folder) named by <see cref="FolderVariable"/>. Unset, /next/ does not exist.
/// </summary>
public static class UiNextAssets
{
    public const string FolderVariable = "HARNESS_UI_NEXT_DIR";

    /// <summary>
    /// The policy for /next/: .NET's WebAssembly runtime compiles its modules ('wasm-unsafe-eval', which allows
    /// WebAssembly compilation only, not JavaScript eval). Everything else stays same-origin.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; frame-ancestors 'none'";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
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

    /// <summary>The content type /next/ serves a file name with; null for a type it does not serve.</summary>
    public static string? ContentType(string name) => ContentTypes.GetValueOrDefault(Path.GetExtension(name));

    internal static void Map(WebApplication app)
    {
        if (Environment.GetEnvironmentVariable(FolderVariable) is not { Length: > 0 } configured)
        {
            return;
        }
        var root = Path.GetFullPath(configured);
        // routing takes "/next" and "/next/" alike: only the one without the slash is sent to the page
        app.MapMethods(
            "/next",
            ["GET", "HEAD"],
            (HttpContext context) =>
                context.Request.Path.Value is "/next" ? Results.Redirect("/next/") : Serve(context, root, "index.html")
        );
        app.MapMethods(
            "/next/{**path}",
            ["GET", "HEAD"],
            (HttpContext context, string? path) => Serve(context, root, path is { Length: > 0 } p ? p : "index.html")
        );
    }

    private static IResult Serve(HttpContext context, string root, string path)
    {
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        var file = Path.GetFullPath(Path.Combine(root, path));
        if (
            !file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || ContentType(file) is not { } type
            || !File.Exists(file)
        )
        {
            throw new UiRequestException(404, "404 page not found");
        }
        // revalidated on every load (ETag and Last-Modified answer 304), so a rebuilt bundle is never stale
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Vary = "Accept-Encoding";
        var served = file;
        if (
            context.Request.Headers.AcceptEncoding.ToString().Contains("br", StringComparison.Ordinal)
            && File.Exists(file + ".br")
        )
        {
            served = file + ".br";
            context.Response.Headers.ContentEncoding = "br";
        }
        var info = new FileInfo(served);
        var tag = new EntityTagHeaderValue(
            $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""
        );
        return Results.File(served, type, lastModified: info.LastWriteTimeUtc, entityTag: tag);
    }
}

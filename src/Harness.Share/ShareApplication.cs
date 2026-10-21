using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Harness.Share;

public sealed record ReleaseBuild(string File, string Version, string OS, string Arch, long Size, string SHA256);
public sealed record SharedRelease(string Directory, string Version, IReadOnlyList<ReleaseBuild> Builds, IReadOnlyList<string> Extra);
public static partial class ShareApplication
{
    [GeneratedRegex(@"^harness-(.+)-(darwin|linux|windows|freebsd)-(amd64|arm64)\.(tar\.gz|zip)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();
    public static SharedRelease Load(string directory)
    {
        Dictionary<string, string> sums = new(StringComparer.Ordinal);
        var sumsPath = Path.Combine(directory, "SHA256SUMS");
        if (File.Exists(sumsPath) && !IsLink(sumsPath))
        {
            foreach (var line in File.ReadLines(sumsPath))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length is 2)
                { sums[fields[1].TrimStart('*')] = fields[0]; }
            }
        }
        List<ReleaseBuild> builds = [];
        List<string> extra = [];
        var version = "";
        foreach (var path in System.IO.Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            if (IsLink(path))
            { continue; }
            var name = Path.GetFileName(path);
            if (name is "SHA256SUMS" or "RELEASE_NOTES.md")
            { extra.Add(name); continue; }
            var match = ArchiveName().Match(name);
            if (!match.Success || name.Any(c => c is < ' ' or > '~' or '"' or '<' or '>'))
            { continue; }
            if (!sums.TryGetValue(name, out var sum))
            { using var file = File.OpenRead(path); sum = Convert.ToHexStringLower(SHA256.HashData(file)); }
            version = match.Groups[1].Value;
            builds.Add(new(name, version, match.Groups[2].Value, match.Groups[3].Value, new FileInfo(path).Length, sum));
        }
        if (builds.Count is 0)
        { throw new InvalidDataException(directory + ": no harness archives (harness-<version>-<os>-<arch>.tar.gz or .zip); build a release first"); }
        return new(Path.GetFullPath(directory), version, builds.OrderBy(b => b.OS + b.Arch, StringComparer.Ordinal).ToArray(), extra);
    }
    public static string Latest(string dist) => System.IO.Directory.EnumerateDirectories(dist).Where(d => !IsLink(d) && HasRelease(d)).OrderByDescending(System.IO.Directory.GetLastWriteTimeUtc).FirstOrDefault() ?? throw new InvalidDataException("no release under " + dist + ": build a release first");
    private static bool HasRelease(string path)
    {
        try
        { Load(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) is not 0;
    public static WebApplication Create(SharedRelease release, string address = "", bool lan = false)
    {
        ArgumentNullException.ThrowIfNull(release);
        var app = CompanionHost.Create(address, lan, 8765);
        var files = release.Builds.ToDictionary(b => "/download/" + b.File, b => b.File, StringComparer.Ordinal);
        foreach (var name in release.Extra)
        { files["/" + name] = name; }
        app.Use(async (context, next) =>
        {
            if (context.Request.Method is not ("GET" or "HEAD"))
            { context.Response.StatusCode = 405; context.Response.Headers.Allow = "GET, HEAD"; await context.Response.WriteAsync("read-only\n"); return; }
            await next(context);
        });
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext context) => CompanionHost.Html(context, "Download harness " + release.Version, Page(release, context.Request.Headers.UserAgent.ToString())));
        app.MapMethods("/{**path}", ["GET", "HEAD"], (HttpContext context) =>
        {
            if (!files.TryGetValue(context.Request.Path.Value ?? "", out var name))
            { return Results.NotFound(); }
            var file = Path.Combine(release.Directory, name);
            if (!File.Exists(file) || IsLink(file))
            { return Results.NotFound(); }
            return Results.File(file, "application/octet-stream", name.StartsWith("harness-", StringComparison.Ordinal) ? name : null, File.GetLastWriteTimeUtc(file), enableRangeProcessing: true);
        });
        return app;
    }
    private static string Label(ReleaseBuild build) => (build.OS switch { "darwin" => "macOS", "linux" => "Linux", "windows" => "Windows", _ => "FreeBSD" }) + " · " + ((build.OS, build.Arch) switch { ("darwin", "arm64") => "Apple silicon", ("darwin", _) => "Intel", (_, "amd64") => "x64", _ => "ARM64" });
    private static string Page(SharedRelease release, string userAgent)
    {
        var os = userAgent switch
        {
            _ when userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "windows",
            _ when userAgent.Contains("Mac", StringComparison.OrdinalIgnoreCase) => "darwin",
            _ when userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) => "linux",
            _ => "",
        };
        var arch = os is "darwin" || userAgent.Contains("arm64", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "amd64";
        var yours = release.Builds.FirstOrDefault(b => b.OS == os && b.Arch == arch) ?? release.Builds.FirstOrDefault(b => b.OS == os);
        var html = new StringBuilder("<h1>Download harness <span class=\"pill\">").Append(WebUtility.HtmlEncode(release.Version)).Append("</span></h1>");
        if (yours is not null)
        {
            html.Append("<h2>Your system</h2><a href=\"/download/").Append(WebUtility.HtmlEncode(yours.File)).Append("\">Download for ").Append(Label(yours)).Append("</a>");
            if (os is "darwin")
            { html.Append("<p>Intel Mac? Choose Intel under macOS below.</p>"); }
        }
        foreach (var build in release.Builds)
        { html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<section><h2>{Label(build)}</h2><a href=\"/download/{WebUtility.HtmlEncode(build.File)}\">{WebUtility.HtmlEncode(build.File)}</a><p>{build.Size / 1e6:F1} MB · sha256 {WebUtility.HtmlEncode(build.SHA256)}</p></section>"); }
        foreach (var extra in release.Extra)
        { html.Append($"<p><a href=\"/{extra}\">{(extra is "RELEASE_NOTES.md" ? "Release notes" : extra)}</a></p>"); }
        return html.Append("<h2>Install</h2><p>Extract the archive. On Linux/macOS: <code>chmod +x harness harness-graphify</code>. Move the binaries to a directory on PATH, then run <code>harness install</code> and <code>harness doctor</code>.</p>").ToString();
    }
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CompanionHost.Options(args, "dir", "addr", "lan", "help", "h");
            if (options.ContainsKey("help") || options.ContainsKey("h"))
            { Console.WriteLine("Usage: harness-share [--dir DIR] [--addr HOST:PORT] [--lan]\nServes a release directory (read-only) until stopped with Ctrl-C."); return 0; }
            var release = Load(options.GetValueOrDefault("dir") ?? Latest("dist"));
            await using var app = Create(release, options.GetValueOrDefault("addr", ""), options.GetValueOrDefault("lan") is "true");
            await app.StartAsync();
            Console.WriteLine($"Serving harness {release.Version} ({release.Builds.Count} builds) from {release.Directory}\n  {UiHost.Address(app)}\nRead-only; stop with Ctrl-C.");
            await app.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { await Console.Error.WriteLineAsync("harness-share: " + ex.Message); return 1; }
    }
}

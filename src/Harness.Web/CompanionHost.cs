using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Web;

public static class CompanionHost
{
    public static WebApplication Create(string address, bool lan, int defaultPort)
    {
        var host = lan ? "0.0.0.0" : "127.0.0.1";
        address = address.Length is 0 ? $"{host}:{defaultPort}" : address;
        if (
            !Uri.TryCreate("http://" + address, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath is not "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || !int.TryParse(address[(address.LastIndexOf(':') + 1)..], out var port)
            || port is < 0 or > 65535
        )
        {
            throw new ArgumentException("--addr must contain a host and port", nameof(address));
        }
        var ip = uri.Host is "localhost" ? IPAddress.Loopback : IPAddress.Parse(uri.Host.Trim('[', ']'));
        if (!lan && !IPAddress.IsLoopback(ip))
        {
            throw new ArgumentException("non-loopback addresses require --lan", nameof(address));
        }
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }
        );
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = (1 << 20) + 1;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Listen(ip, port);
        });
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                var path = (context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "").Split('?')[0];
                if (
                    path.Contains('%', StringComparison.Ordinal)
                    || path.Contains('\\', StringComparison.Ordinal)
                    || path.Split('/').Any(p => p is "." or "..")
                )
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                try
                {
                    await next(context);
                }
                catch (Exception ex)
                    when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync("could not process request");
                }
            }
        );
        app.MapMethods(
            "/logo.svg",
            ["GET", "HEAD"],
            () => Results.Bytes(UiAssets.Read("Assets/logo.svg"), "image/svg+xml")
        );
        app.MapMethods(
            "/favicon.png",
            ["GET", "HEAD"],
            () => Results.Bytes(UiAssets.Read("Assets/logo.png"), "image/png")
        );
        return app;
    }

    private static string Style =>
        System.Text.Encoding.UTF8.GetString(UiAssets.Read("Assets/brand.css"))
        + " main{max-width:1100px;margin:auto;padding:2rem}td,th{padding:1rem}";

    public static string Page(string title, string body) =>
        "<!doctype html><html lang=\"en\" data-theme=\"dark\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>"
        + WebUtility.HtmlEncode(title)
        + "</title><link rel=\"icon\" href=\"/logo.svg\"><style>"
        + Style
        + "</style><main>"
        + System.Text.Encoding.UTF8.GetString(UiAssets.Read("Assets/logo.svg"))
        + body
        + "</main></html>";

    public static Dictionary<string, string> Options(string[] args, params string[] allowed)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        var i = 0;
        while (i < args.Length)
        {
            var parts = args[i].TrimStart('-').Split('=', 2);
            if (!allowed.Contains(parts[0], StringComparer.Ordinal))
            {
                throw new ArgumentException("unknown option: " + parts[0], nameof(args));
            }
            string value;
            if (parts.Length > 1)
            {
                value = parts[1];
            }
            else if (parts[0] is "lan" or "help" or "h")
            {
                value = "true";
            }
            else
            {
                i++;
                value = i < args.Length ? args[i] : throw new ArgumentException("missing option value", nameof(args));
            }
            values[parts[0]] = value;
            i++;
        }
        return values;
    }

    public static IResult Html(HttpContext context, string title, string body)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'sha256-"
            + Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Style))
            )
            + "'; img-src 'self'";
        return Results.Content(Page(title, body), "text/html; charset=utf-8");
    }
}

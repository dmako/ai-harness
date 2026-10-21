using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Web;

public static class UiHost
{
    public const string DefaultAddress = "127.0.0.1:17477";
    public static bool IsLoopback(string host) => host is "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    public static (IPAddress Address, int Port) ParseAddress(string address)
    {
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.AbsolutePath is not "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !IsLoopback(uri.Host) || !address.Contains(':', StringComparison.Ordinal) || !int.TryParse(address[(address.LastIndexOf(':') + 1)..], out var port) || port is < 0 or > 65535)
        {
            throw new ArgumentException("--addr must be a loopback host and port, e.g. " + DefaultAddress, nameof(address));
        }
        return (uri.Host is "localhost" ? IPAddress.Loopback : IPAddress.Parse(uri.Host.Trim('[', ']')), port);
    }
    public static WebApplication Create(string address, Func<IServiceProvider, IUiReadApi> api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var (ip, port) = ParseAddress(address);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, ApplicationName = typeof(UiHost).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MaxRequestBodySize = 1 << 16;
            options.Listen(ip, port);
        });
        builder.Services.AddScoped(api);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopback(context.Request.Host.Host))
            {
                await ErrorAsync(context, 403, "only loopback hosts are served");
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'";
            var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
            var path = raw.Split('?')[0];
            if (path.Contains('%', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) || path.Split('/').Any(p => p is "." or ".."))
            {
                await ErrorAsync(context, 400, "invalid request path");
                return;
            }
            try
            {
                await next(context);
            }
            catch (UiRequestException exception)
            {
                await ErrorAsync(context, exception.Status, exception.Message);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { /* The caller disconnected. */ }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
            {
                await ErrorAsync(context, 500, exception.Message);
            }
        });
        foreach (var route in UiRoutes.Reads)
        {
            var endpoint = route;
            app.MapMethods(route, ["GET", "HEAD"], async (HttpContext context, IUiReadApi service) =>
            {
                var request = new UiReadRequest(endpoint, context.Request.RouteValues.ToDictionary(v => v.Key, v => v.Value?.ToString() ?? "", StringComparer.Ordinal), context.Request.Query.ToDictionary(v => v.Key, v => v.Value.ToString(), StringComparer.Ordinal), context.Request.Headers["X-Harness-UI"] is var value && value.Count is 1 && value[0] is "1");
                var response = await service.ReadAsync(request, context.RequestAborted);
                return Results.Text(response?.ToJsonString() ?? "null", "application/json; charset=utf-8");
            });
        }
        UiAssets.Map(app);
        app.MapMethods("/api/{**rest}", ["GET", "HEAD"], (HttpContext context) => ErrorAsync(context, 404, "unknown endpoint"));
        app.MapFallback((HttpContext context) => ErrorAsync(context, 404, "404 page not found"));
        return app;
    }
    public static string Address(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";
    }
    private static Task ErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new JsonObject { ["error"] = message }, context.RequestAborted);
    }
}

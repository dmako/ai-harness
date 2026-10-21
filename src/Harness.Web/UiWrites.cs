using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Harness.Web;

internal static class UiWrites
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static UiReadRequest Route(HttpContext context, string endpoint) => new(endpoint,
        context.Request.RouteValues.ToDictionary(v => v.Key, v => v.Value?.ToString() ?? "", StringComparer.Ordinal),
        context.Request.Query.ToDictionary(v => v.Key, v => v.Value.Count > 0 ? v.Value[0] ?? "" : "", StringComparer.Ordinal), true);
    internal static void Map(WebApplication app)
    {
        foreach (var route in UiRoutes.Writes)
        {
            var endpoint = route;
            app.MapPost(route, async (HttpContext context, IUiWriteApi service) =>
            {
                Check(context.Request);
                var body = await BodyAsync(context.Request);
                var result = await service.WriteAsync(new(Route(context, endpoint), body), context.RequestAborted);
                return Results.Text(result?.ToJsonString() ?? "null", "application/json; charset=utf-8");
            });
        }
    }
    private static void Check(HttpRequest request)
    {
        if (request.Headers["X-Harness-UI"] is not { Count: 1 } ui || ui[0] is not "1")
        { throw new UiRequestException(403, "writes need the X-Harness-UI: 1 header"); }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var content) || !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        { throw new UiRequestException(403, "writes need a JSON body (Content-Type: application/json)"); }
        if (request.Headers.Origin is not { Count: 1 } origin || origin[0] != "http://" + request.Host.Value)
        { throw new UiRequestException(403, "writes must come from this page"); }
    }
    private static async Task<JsonObject> BodyAsync(HttpRequest request)
    {
        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await request.Body.ReadAsync(bytes, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + count > 65536)
                { throw new UiRequestException(400, "invalid request body: exceeds 64 KiB"); }
                await buffer.WriteAsync(bytes.AsMemory(0, count), request.HttpContext.RequestAborted);
            }
            return JsonNode.Parse(StrictUtf8.GetString(buffer.ToArray())) as JsonObject ?? throw new UiRequestException(400, "invalid request body: expected an object");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or BadHttpRequestException)
        { throw new UiRequestException(400, "invalid request body: " + exception.Message); }
    }
}

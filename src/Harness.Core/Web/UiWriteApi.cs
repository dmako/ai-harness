using System.Text.Json.Nodes;

namespace Harness.Core.Web;

public sealed record UiWriteRequest(UiReadRequest Route, JsonObject Body);
public interface IUiWriteApi
{
    Task<JsonNode?> WriteAsync(UiWriteRequest request, CancellationToken token);
}
public interface IUiChangeSource : IAsyncDisposable
{
    Task<JsonObject> PollAsync(CancellationToken token);
}

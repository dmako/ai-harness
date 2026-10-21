using System.Text.Json.Nodes;

namespace Harness.Core.Web;

public sealed record UiReadRequest(string Endpoint, IReadOnlyDictionary<string, string> Route, IReadOnlyDictionary<string, string> Query, bool Trusted)
{
    public string Parameter(string name) => Query.GetValueOrDefault(name, "");
    public string Value(string name) => Route.GetValueOrDefault(name, "");
}
public interface IUiReadApi
{
    Task<JsonNode?> ReadAsync(UiReadRequest request, CancellationToken token);
}
public sealed class UiRequestException : Exception
{
    public UiRequestException(int status, string message) : base(message) { Status = status; }
    public UiRequestException() : this(500, "UI request failed") { }
    public UiRequestException(string message) : this(500, message) { }
    public UiRequestException(string message, Exception innerException) : base(message, innerException) { }
    public int Status { get; } = 500;
}

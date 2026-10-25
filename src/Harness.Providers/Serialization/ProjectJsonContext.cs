using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Harness.Core.Serialization;

namespace Harness.Providers.Serialization;

[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Diagnostic))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Edge))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Location))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Page))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.RemoteError))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Response))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Scope))]
[JsonSerializable(typeof(Harness.Providers.Graphify.GraphifyProvider.Symbol))]
[JsonSerializable(typeof(Harness.Providers.Mcp.McpMapping.Entry))]
[JsonSerializable(typeof(Harness.Providers.Mcp.McpProcessOptions))]
[JsonSerializable(typeof(Harness.Providers.Mcp.McpRestartPolicy))]
[JsonSerializable(typeof(Harness.Providers.Protocol.ProtocolCapability))]
[JsonSerializable(typeof(Harness.Providers.Protocol.ProtocolResponse))]
[JsonSerializable(typeof(Harness.Providers.Protocol.ProtocolServer.CapabilityRequest))]
[JsonSerializable(typeof(Harness.Providers.Protocol.ProviderInfo))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(System.DateTime))]
[JsonSerializable(typeof(System.DateTimeOffset))]
[JsonSerializable(typeof(System.Guid))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, object>))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class ProjectJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255",
        Justification = "Register the assembly's generated wire metadata before any serialization entry point runs."
    )]
    internal static void Register() => HarnessJson.Register(Default);
}

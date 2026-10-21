using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.ProviderWorker;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
if (Environment.GetEnvironmentVariable("HARNESS_FAKE_GRAPHIFY") is { Length: > 0 } graphifyDirectory)
{
    Environment.ExitCode = await GraphifyWorker.RunAsync(graphifyDirectory, args);
    return;
}
var mode = args.FirstOrDefault() ?? "normal";
if (mode is "descendant")
{
    await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    return;
}
var directory = args.Length > 1 ? args[1] : "";
if (mode is "stderr")
{
    await Console.Error.WriteAsync(new string('x', 1024 * 1024));
    await Console.Error.WriteLineAsync("\nline-1\nline-2");
    await Console.Error.WriteLineAsync(Environment.GetEnvironmentVariable("HARNESS_PROVIDER_STDERR_FIXTURE") ?? "line-3");
    await Console.Error.WriteLineAsync("line-4\nlast-line");
    await Console.Error.FlushAsync();
}
if (mode is "startup-spawn-exit")
{
    await SpawnAsync(directory);
    return;
}
using var writes = new SemaphoreSlim(1, 1);
while (await Console.In.ReadLineAsync() is { } line)
{
    _ = HandleAsync(line);
}

if (mode is "ignore-eof")
{
    await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
}

async Task HandleAsync(string line)
{
    try
    {
        var request = JsonNode.Parse(line)!.AsObject();
        var id = request["id"]?.DeepClone();
        if (id is null)
        {
            return;
        }

        var method = request["method"]!.GetValue<string>();
        JsonNode result;
        if (method is "initialize")
        {
            if (mode is "stall-start")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            }

            if (mode is "fail-start")
            {
                await Console.Error.WriteLineAsync("cannot initialize fixture");
                Environment.Exit(23);
            }
            result = new JsonObject { ["protocolVersion"] = request["params"]!["protocolVersion"]!.DeepClone(), ["serverInfo"] = new JsonObject { ["name"] = "provider-fixture", ["version"] = "1.0" }, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() } };
        }
        else if (method is "tools/list")
        {
            var cursor = request["params"]?["cursor"]?.GetValue<string>();
            var page = Path.Combine(directory, cursor is null ? "tools-list.page" : "tools-list-next.page");
            if (File.Exists(page))
            {
                result = JsonNode.Parse(await File.ReadAllTextAsync(page))!;
            }
            else
            {
                var names = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).Select(name => name!.Replace(".result", "", StringComparison.Ordinal)) : [];
                var tools = names.Concat(new[] { "echo", "hang", "crash", "oversized", "spawn", "exit-after-spawn" }).Distinct(StringComparer.Ordinal).Select(name => (JsonNode)new JsonObject { ["name"] = name, ["inputSchema"] = new JsonObject { ["type"] = "object" } }).ToArray();
                result = new JsonObject { ["tools"] = new JsonArray(tools) };
            }
        }
        else if (method is "tools/call")
        {
            var tool = request["params"]!["name"]!.GetValue<string>();
            if (tool != Path.GetFileName(tool))
            {
                throw new InvalidDataException("Invalid fixture tool name.");
            }

            var arguments = request["params"]?["arguments"]?.DeepClone() ?? new JsonObject();
            if (Directory.Exists(directory))
            {
                await writes.WaitAsync(CancellationToken.None);
                try
                { await File.WriteAllTextAsync(Path.Combine(directory, tool + ".args"), arguments.ToJsonString()); }
                finally { writes.Release(); }
            }
            if (tool is "crash")
            {
                Environment.Exit(17);
            }

            if (tool is "hang")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            }

            if (tool is "oversized")
            {
                await Console.Out.WriteAsync(new string('x', 17 * 1024 * 1024));
                await Console.Out.FlushAsync();
                return;
            }
            var delay = Path.Combine(directory, tool + ".delay");
            if (File.Exists(delay))
            {
                await Task.Delay(int.Parse(await File.ReadAllTextAsync(delay), CultureInfo.InvariantCulture), CancellationToken.None);
            }

            if (tool is "spawn" or "exit-after-spawn")
            {
                await SpawnAsync(arguments["pid_file"]!.GetValue<string>());
            }

            var protocolError = Path.Combine(directory, tool + ".protocol-error");
            if (File.Exists(protocolError))
            {
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = await File.ReadAllTextAsync(protocolError) } });
                return;
            }
            var fullResult = Path.Combine(directory, tool + ".result.json");
            if (File.Exists(fullResult))
            {
                result = JsonNode.Parse(await File.ReadAllTextAsync(fullResult))!;
            }
            else
            {
                var fixture = Path.Combine(directory, tool + ".json");
                var body = File.Exists(fixture) ? await File.ReadAllTextAsync(fixture) : JsonSerializer.Serialize(new { arguments, cwd = Environment.CurrentDirectory, environment = Environment.GetEnvironmentVariable("HARNESS_PROVIDER_TEST"), process_arguments = args });
                result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body }) };
                if (File.Exists(Path.Combine(directory, tool + ".error")))
                {
                    result["isError"] = true;
                }
            }
            if (tool is "exit-after-spawn")
            {
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
                Environment.Exit(0);
            }
        }
        else if (method is "ping")
        {
            result = new JsonObject();
        }
        else
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" } });
            return;
        }
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
    }
    catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
    {
        await Console.Error.WriteLineAsync(error.GetType().Name);
    }
}

async Task SendAsync(JsonObject response)
{
    await writes.WaitAsync(CancellationToken.None);
    try
    { await Console.Out.WriteLineAsync(response.ToJsonString()); await Console.Out.FlushAsync(); }
    finally { writes.Release(); }
}

static async Task SpawnAsync(string pidFile)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    }

    start.ArgumentList.Add("descendant");
    start.ArgumentList.Add(pidFile);
    using var child = Process.Start(start)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!File.Exists(pidFile))
    {
        await Task.Delay(10, timeout.Token);
    }
}

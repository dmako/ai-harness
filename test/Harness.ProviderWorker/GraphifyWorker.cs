using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Harness.ProviderWorker;

internal static class GraphifyWorker
{
    internal static async Task<int> RunAsync(string directory, string[] args)
    {
        try
        { return await DispatchAsync(directory, args); }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString().Replace("\r", "", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal));
            return 5;
        }
    }

    private static async Task<int> DispatchAsync(string directory, string[] args)
    {
        using (await LockAsync(directory))
        {
            await File.AppendAllTextAsync(Path.Combine(directory, "calls.log"), new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()).ToJsonString() + "\n");
        }

        if (args.Length is 0)
        {
            return 2;
        }

        switch (args[0])
        {
            case "ps":
                // Readers share the fake's writer lease so Windows replacement never races an open fixture read.
                using (await LockAsync(directory))
                {
                    await Console.Out.WriteAsync((await ReadAsync(directory)).ToJsonString());
                }

                return 0;
            case "watch":
                return await WatchAsync(directory, args);
            case "stop":
                using (await LockAsync(directory))
                {
                    var sessions = await ReadAsync(directory);
                    var array = sessions["sessions"]!.AsArray();
                    foreach (var session in array.Where(node => node?["session_id"]?.GetValue<string>() == args[1]).ToArray())
                    {
                        array.Remove(session);
                    }

                    await WriteAsync(directory, sessions);
                    File.Delete(Path.Combine(directory, "descriptors", args[1] + ".json"));
                }
                await Console.Out.WriteAsync("""{"success":true}""");
                return 0;
            case "query":
                return await QueryAsync(directory, args);
            default:
                return 2;
        }
    }

    private static async Task<int> WatchAsync(string directory, string[] args)
    {
        if (File.Exists(Path.Combine(directory, "watch-fails")))
        { await Console.Error.WriteLineAsync("error: no solution or project found under the root"); return 3; }
        var id = "watch-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var began = Stopwatch.GetTimestamp();
        var record = new JsonObject { ["session_id"] = id, ["process_id"] = Environment.ProcessId, ["reachability"] = "reachable", ["lifecycle_state"] = "loading", ["ready"] = false, ["stage"] = "compiling", ["repository_root"] = Argument(args, "--root"), ["input_path"] = Argument(args, "--input"), ["uptime_ms"] = 1 };
        Directory.CreateDirectory(Path.Combine(directory, "descriptors"));
        await File.WriteAllTextAsync(Path.Combine(directory, "descriptors", id + ".json"), record.ToJsonString());
        using (await LockAsync(directory))
        {
            var sessions = await ReadAsync(directory);
            sessions["sessions"]!.AsArray().Add(record.DeepClone());
            await WriteAsync(directory, sessions);
        }
        while (Stopwatch.GetElapsedTime(began) < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(50);
            using (await LockAsync(directory))
            {
                var sessions = await ReadAsync(directory);
                var session = sessions["sessions"]!.AsArray().FirstOrDefault(node => node?["session_id"]?.GetValue<string>() == id);
                if (session is null)
                {
                    return 0;
                }

                var ready = !File.Exists(Path.Combine(directory, "never-ready")) && Stopwatch.GetElapsedTime(began) > TimeSpan.FromMilliseconds(250);
                session["ready"] = ready;
                session["lifecycle_state"] = ready ? "ready" : "loading";
                session["uptime_ms"] = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                await WriteAsync(directory, sessions);
            }
        }
        return 4;
    }

    private static async Task<int> QueryAsync(string directory, string[] args)
    {
        JsonObject sessions;
        using (await LockAsync(directory))
        {
            sessions = await ReadAsync(directory);
        }

        if (!sessions["sessions"]!.AsArray().Any(node => node?["session_id"]?.GetValue<string>() == Argument(args, "--instance")))
        { await Console.Out.WriteAsync("""{"success":false,"error":{"code":"session_unreachable","message":"no such session"},"items":[]}"""); return 1; }
        var symbol = Argument(args, "--symbol");
        var file = Path.Combine(directory, args[1] + (symbol.Length > 0 ? "-" + symbol : "") + ".json");
        var delay = Path.Combine(directory, "query.delay");
        if (File.Exists(delay))
        {
            await Task.Delay(int.Parse(await File.ReadAllTextAsync(delay), CultureInfo.InvariantCulture));
        }

        await Console.Out.WriteAsync(File.Exists(file) ? await File.ReadAllTextAsync(file) : """{"success":true,"items":[]}""");
        return 0;
    }

    private static string Argument(string[] args, string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    private static async Task<JsonObject> ReadAsync(string directory)
    {
        try
        {
            await using var file = new FileStream(Path.Combine(directory, "sessions.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (await JsonNode.ParseAsync(file))!.AsObject();
        }
        catch (FileNotFoundException) { return new() { ["success"] = true, ["sessions"] = new JsonArray() }; }
    }
    private static async Task WriteAsync(string directory, JsonObject sessions)
    {
        var file = Path.Combine(directory, "sessions.json");
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, sessions.ToJsonString());
        File.Move(temporary, file, overwrite: true);
    }
    private static async Task<FileStream> LockAsync(string directory)
    {
        var began = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            { return new FileStream(Path.Combine(directory, "fake.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
        }
    }
}

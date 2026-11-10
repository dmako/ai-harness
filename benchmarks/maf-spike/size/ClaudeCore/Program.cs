// TASK-0112 size probe: Claude Code through ClaudeCodeSdk's own client, without Microsoft Agent Framework.
using ClaudeCodeSdk;
using ClaudeCodeSdk.Types;

await using var client = new ClaudeSdkClient(
    new ClaudeCodeOptions
    {
        Model = "haiku",
        WorkingDirectory = args[0],
        PermissionMode = PermissionMode.plan,
        MaxTurns = 1,
    }
);
await client.ConnectAsync();
await client.QueryAsync("Reply with exactly the word READY.");
await foreach (var message in client.ReceiveResponseAsync())
{
    if (message is ResultMessage result)
    {
        System.Console.WriteLine(
            $"{result.Result} session {result.SessionId} cost ${result.TotalCostUsd:F4} in {result.Usage?.InputTokens} out {result.Usage?.OutputTokens}"
        );
    }
}

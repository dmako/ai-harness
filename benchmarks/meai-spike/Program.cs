// TASK-0111: should harness's provider layer use Microsoft.Extensions.AI? See README.md.
using System.Globalization;
using MeaiSpike;

return args switch
{
    ["serve", var bytes] => await FakeServer.RunAsync(int.Parse(bytes, CultureInfo.InvariantCulture)),
    ["bench", _, _, _, ..] => await Bench.RunAsync(args[1..]),
    ["compose", ..] => await Compose.RunAsync(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        """
        MeaiSpike bench <rounds> <tool> <arguments json> fake <bytes>
        MeaiSpike bench <rounds> <tool> <arguments json> -- <command> [args...]
                      one tool call three ways: harness's path, McpClientTool as AIFunction, and behind a DelegatingAIFunction
        MeaiSpike compose fake <bytes>
                      fallback and union composites, FunctionInvokingChatClient with a scripted model, embedding rerank
        MeaiSpike serve <bytes>
                      the fake MCP server the other commands start
        """
    );
    return 2;
}

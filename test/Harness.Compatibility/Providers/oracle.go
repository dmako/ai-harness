// Runs the pinned generic adapter against the same controlled MCP subprocess as C#.
package main

import (
    "context"
    "encoding/json"
    "os"
    "harness/internal/config"
    "harness/internal/providers"
    "harness/internal/providers/mcpgeneric"
)

func main() {
    var input struct { Root string; Name string; Configuration config.ProviderConfig; Request providers.Request }
    b, err := os.ReadFile(os.Args[1]); if err != nil { panic(err) }
    if err = json.Unmarshal(b, &input); err != nil { panic(err) }
    p := mcpgeneric.Build(input.Name, input.Configuration, input.Root)
    defer p.Close()
    result, failure := p.Do(context.Background(), input.Request)
    items := result.Items
    if items == nil { items = []providers.Item{} } // Internal collection only; not a public MCP envelope.
    message := ""; if failure != nil { message = failure.Error() }
    output := map[string]any{
        "capabilities":p.Capabilities(), "items":items, "summary":result.Summary,
        "text":result.Text, "truncated":result.Truncated, "bytes":result.Bytes, "error":message,
    }
    if err = json.NewEncoder(os.Stdout).Encode(output); err != nil { panic(err) }
}

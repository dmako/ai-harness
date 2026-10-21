// Runs the frozen protocol client and public facade against controlled MCP servers.
package main

import (
    "context"
    "encoding/json"
    "os"
    "github.com/modelcontextprotocol/go-sdk/mcp"
    "harness/internal/config"
    "harness/internal/mcp/server"
    "harness/internal/providers"
    "harness/internal/providers/protocol"
)

func main() {
    var in struct { Root string; Name string; Configuration config.ProviderConfig; Request providers.Request; Tool string; Arguments map[string]any }
    b, err := os.ReadFile(os.Args[1]); if err != nil { panic(err) }
    if err = json.Unmarshal(b, &in); err != nil { panic(err) }
    p := protocol.New(in.Name, in.Configuration, in.Root)
    defer p.Close()
    ctx := context.Background()
    var output any
    if in.Tool != "" {
        reg := providers.NewRegistry(0); reg.Register(p)
        s := server.New(server.Options{ProjectRoot:in.Root, Registry:reg})
        defer s.Close()
        a, b := mcp.NewInMemoryTransports()
        session, err := s.MCP().Connect(ctx, a, nil); if err != nil { panic(err) }; defer session.Close()
        client, err := mcp.NewClient(&mcp.Implementation{Name:"oracle", Version:"test"}, nil).Connect(ctx, b, nil); if err != nil { panic(err) }; defer client.Close()
        result, err := client.CallTool(ctx, &mcp.CallToolParams{Name:in.Tool, Arguments:in.Arguments}); if err != nil { panic(err) }
        if result.IsError { output=map[string]any{"error":result.Content[0].(*mcp.TextContent).Text} } else { output=result.StructuredContent }
    } else {
        caps := p.Capabilities()
        if caps == nil { caps=[]providers.CapabilityInfo{} }
        result, failure := p.Do(ctx, in.Request)
        items := result.Items; if items == nil { items=[]providers.Item{} }
        message := ""; if failure != nil { message=failure.Error() }
        output=map[string]any{"capabilities":caps,"items":items,"summary":result.Summary,"text":result.Text,"truncated":result.Truncated,"bytes":result.Bytes,"error":message}
    }
    if err = json.NewEncoder(os.Stdout).Encode(output); err != nil { panic(err) }
}

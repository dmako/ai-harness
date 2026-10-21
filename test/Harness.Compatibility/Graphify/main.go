// Frozen adapter exercised against the same independent graphify CLI as C#.
package main

import (
    "context"
    "encoding/json"
    "os"
    "harness/internal/providers"
)

func main() {
    var in struct { Root string; Command string; Request providers.Request }
    b, err := os.ReadFile(os.Args[1]); if err != nil { panic(err) }
    if err=json.Unmarshal(b, &in); err != nil { panic(err) }
    p := New(Options{Root:in.Root, Command:in.Command})
    defer p.Close()
    r, failure := p.Do(context.Background(), in.Request)
    items := r.Items; if items == nil { items=[]providers.Item{} }
    message := ""; if failure != nil { message=failure.Error() }
    output := map[string]any{"capabilities":p.Capabilities(),"items":items,"truncated":r.Truncated,"bytes":r.Bytes,"error":message}
    if err=json.NewEncoder(os.Stdout).Encode(output); err != nil { panic(err) }
}

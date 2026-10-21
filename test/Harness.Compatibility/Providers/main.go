// Captures registry contracts using the frozen Go module, never the installed harness.
package main

import (
    "context"
    "encoding/json"
    "os"
    "harness/internal/providers"
)

type fake struct { name string; caps []providers.Capability }
func (f *fake) Name() string { return f.name }
func (f *fake) Capabilities() []providers.CapabilityInfo {
    out := []providers.CapabilityInfo{}
    for _, c := range f.caps { out = append(out, providers.CapabilityInfo{Capability:c, Provider:f.name, Cost:providers.Low, Latency:providers.Medium, Freshness:providers.Indexed, Scope:providers.ScopeProject}) }
    return out
}
func (f *fake) Health(context.Context, bool) providers.Health { return providers.Health{State:providers.Idle} }
func (f *fake) Do(_ context.Context, req providers.Request) (providers.Result, error) {
    result := providers.Result{Bytes:123}
    for _, path := range []string{"src/a.cs", "src/b.cs", "src2/c.cs"} {
        if req.Covers(path) { result.Items = append(result.Items, providers.Item{Kind:providers.KindMatch, Path:path, Provider:f.name, Tool:"find"}) }
    }
    if req.Limit > 0 && len(result.Items) > req.Limit { result.Items = result.Items[:req.Limit]; result.Truncated = true }
    return result, nil
}
func (*fake) Close() error { return nil }

func main() {
    all := []providers.Capability{providers.SymbolSearch, providers.TextSearch, providers.FileSearch, providers.Overview, providers.Callers, providers.References, providers.Context, providers.Impact, providers.Flow, providers.PackageSearch, providers.PackageInfo, providers.PackageTypes, providers.PackageTypeDefinition}
    all = append(all, providers.ArchCapabilities...)
    metadata := []any{}
    for _, c := range all { metadata = append(metadata, map[string]any{"capability":c, "evidence":providers.DefaultEvidence(c), "package":c.IsPackage(), "architecture":c.IsArch()}) }
    registry := providers.NewRegistry(0)
    registry.Register(&fake{"z", []providers.Capability{providers.TextSearch}})
    registry.Register(&fake{"a", []providers.Capability{providers.SymbolSearch, providers.TextSearch}})
    registry.ExcludeFromContext("z")
    answers := []any{}
    for _, result := range registry.CallExcept(context.Background(), providers.Request{Capability:providers.TextSearch, Path:"src", Limit:1}, nil) {
        answers = append(answers, map[string]any{"provider":result.Provider, "capability":result.Capability, "items":result.Items, "bytes":result.Bytes, "truncated":result.Truncated})
    }
    paths := []any{}
    for _, pair := range [][2]string{{"", "any.cs"}, {"src", "src"}, {"src/", "src/a.cs"}, {"src", "src2/a.cs"}, {"src", "other/a.cs"}} {
        paths = append(paths, map[string]any{"prefix":pair[0], "file":pair[1], "covers":(providers.Request{Path:pair[0]}).Covers(pair[1])})
    }
    item := providers.Item{Kind:providers.KindRelation, Name:"A", QualifiedName:"A.B", Label:"project", Path:"A.csproj", StartLine:2, EndLine:3, Snippet:"reference", Score:0.7, Depth:1, Related:&providers.Ref{Name:"B", Path:"B.csproj", StartLine:4}, Members:[]string{"A", "B"}, Count:8, Confidence:0.9, Provider:"compiler", Tool:"dependencies"}
    output := map[string]any{"metadata":metadata, "capabilities":registry.Capabilities(), "answers":answers, "paths":paths, "item":item, "empty_item":providers.Item{}, "health":providers.Health{State:providers.Idle}, "in_context":map[string]bool{"z":registry.InContext("z"), "a":registry.InContext("a")}}
    encoder := json.NewEncoder(os.Stdout)
    encoder.SetIndent("", "  ")
    if err := encoder.Encode(output); err != nil { panic(err) }
}

// Contract driver only: all retrieval algorithms come from the frozen source.
package main

import (
    "context"
    "encoding/json"
    "errors"
    "os"
    "time"

    "harness/internal/providers"
    "harness/internal/retrieval"
)

type provider struct {
    ID string
    Answers map[string][]providers.Item
    Failure string
    Excluded bool
}
func (p *provider) Name() string { return p.ID }
func (p *provider) Capabilities() []providers.CapabilityInfo {
    out := []providers.CapabilityInfo{}
    for _, c := range []providers.Capability{providers.SymbolSearch, providers.Context, providers.Callers, providers.Impact, providers.FileSearch} {
        out = append(out, providers.CapabilityInfo{Capability:c, Provider:p.ID})
    }
    return out
}
func (p *provider) Health(context.Context, bool) providers.Health { return providers.Health{} }
func (p *provider) Close() error { return nil }
func (p *provider) Do(ctx context.Context, req providers.Request) (providers.Result,error) {
    if p.Failure != "" { return providers.Result{}, errors.New(p.Failure) }
    items := append([]providers.Item{}, p.Answers[string(req.Capability)+"|"+req.Query]...)
    for i := range items { items[i].Provider=p.ID; items[i].Tool="fixture" }
    return providers.Result{Items:items},nil
}

func list[T any](s []T) []T { if s==nil { return []T{} }; return s }
func candidate(c retrieval.Candidate) map[string]any {
    provenance := []any{}
    for _,p := range c.Provenance { provenance=append(provenance,map[string]any{"provider":p.Provider,"tool":p.Tool,"capability":p.Capability,"rank":p.Rank}) }
    scores:=c.Scores; if scores==nil { scores=map[string]float64{} }
    return map[string]any{"id":c.ID,"source":c.Source,"role":c.Role,"name":c.Name,"qualifiedName":c.QualifiedName,"path":c.Path,"startLine":c.StartLine,"endLine":c.EndLine,"text":c.Text,"size":c.Size,"sizeEstimated":c.SizeEstimated,"depth":c.Depth,"scores":scores,"evidence":c.Evidence,"freshness":c.Freshness,"when":c.When.Format(time.RFC3339Nano),"provenance":provenance,"memoryScope":c.MemoryScope,"overrides":list(c.Overrides)}
}
func candidates(cs []retrieval.Candidate) []any { out:=[]any{}; for _,c:=range cs { out=append(out,candidate(c)) }; return out }

func main() {
    var input struct {
        Text string
        Candidates []retrieval.Candidate
        Items []struct { Info providers.CapabilityInfo; Rank int; Item providers.Item }
        Providers []*provider
    }
    if err:=json.NewDecoder(os.Stdin).Decode(&input); err!=nil { panic(err) }
    normalized:=[]retrieval.Candidate{}
    for _,item:=range input.Items { normalized=append(normalized,retrieval.FromItem(item.Info,item.Rank,item.Item)) }
    merged,folded:=retrieval.MergeReport(append(input.Candidates,normalized...))
    folds:=[]any{}
    for _,f:=range folded { folds=append(folds,map[string]any{"candidate":candidate(f.Candidate),"into":f.Into,"reason":f.Reason}) }
    ranked:=[]any{}
    for _,r:=range retrieval.Rank(merged) { ranked=append(ranked,map[string]any{"candidate":candidate(r.Candidate),"tier":r.Tier,"score":r.Score,"why":r.Why}) }
    registry:=providers.NewRegistry(0)
    for _,p:=range input.Providers { registry.Register(p); if p.Excluded { registry.ExcludeFromContext(p.ID) } }
    gathered:=retrieval.Gather(context.Background(),retrieval.Sources{Registry:registry},retrieval.Task{Text:input.Text})
    calls:=[]any{}
    for _,c:=range gathered.Calls { calls=append(calls,map[string]any{"provider":c.Provider,"capability":c.Capability,"query":c.Query,"items":c.Items,"bytes":c.Bytes,"error":c.Error}) }
    output:=map[string]any{"normalized":candidates(normalized),"merged":candidates(merged),"folded":folds,"ranked":ranked,"gathered":map[string]any{"candidates":candidates(gathered.Candidates),"symbols":list(gathered.Symbols),"phrases":list(gathered.Phrases),"errors":list(gathered.Errors),"notes":list(gathered.Notes),"calls":calls}}
    if err:=json.NewEncoder(os.Stdout).Encode(output); err!=nil { panic(err) }
}

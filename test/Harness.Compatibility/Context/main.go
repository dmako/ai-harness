package main

import (
    "context"
    "encoding/json"
    "fmt"
    "os"
    "path/filepath"
    "harness/internal/providers"
    "harness/internal/retrieval"
    "harness/internal/mcp/server"
)
type provider struct { Items []providers.Item }
func (p *provider) Name() string { return "fixture" }
func (p *provider) Capabilities() []providers.CapabilityInfo { return []providers.CapabilityInfo{{Provider:p.Name(),Capability:providers.SymbolSearch}} }
func (p *provider) Health(context.Context,bool) providers.Health { return providers.Health{} }
func (p *provider) Close() error { return nil }
func (p *provider) Do(context.Context,providers.Request) (providers.Result,error) { return providers.Result{Items:p.Items},nil }
func main() {
    var in struct { Mode string; Candidates []retrieval.Candidate; Budget retrieval.Budget; Bodies map[string]string; Items []providers.Item; Request json.RawMessage }
    if err:=json.NewDecoder(os.Stdin).Decode(&in); err!=nil { panic(err) }
    if in.Mode=="render" {
        root,err:=os.MkdirTemp("","context-oracle-"); if err!=nil { panic(err) }; defer os.RemoveAll(root)
        for name,body:=range in.Bodies { path:=filepath.Join(root,name); if err:=os.MkdirAll(filepath.Dir(path),0700);err!=nil {panic(err)}; if err:=os.WriteFile(path,[]byte(body),0600);err!=nil {panic(err)} }
        registry:=providers.NewRegistry(0); registry.Register(&provider{in.Items})
        output,err:=server.PortContext(context.Background(),root,registry,in.Request); if err!=nil {panic(err)}
        fmt.Println(string(output)); return
    }
    ranked:=retrieval.Rank(retrieval.Merge(in.Candidates))
    read:=func(path string,start,end int)(string,error) { body,ok:=in.Bodies[path];if !ok{return "",fmt.Errorf("missing")};return body,nil }
    packed:=retrieval.Pack(ranked,in.Budget,read)
    units:=[]any{}; rejected:=[]string{}
    for _,u:=range packed.Units { units=append(units,map[string]any{"id":u.ID,"tier":u.Tier,"score":u.Score,"why":u.Why,"body":u.Body,"clipped":u.Clipped,"reference":u.Reference,"cost":u.Cost,"bodyNote":u.BodyNote}) }
    for _,r:=range packed.Rejected {rejected=append(rejected,r.ID)}
    json.NewEncoder(os.Stdout).Encode(map[string]any{"units":units,"rejected":rejected,"used":packed.Used,"omitted":packed.Omitted,"overrun":packed.Overrun,"note":packed.Note,"budget":map[string]any{"bytes":packed.Budget.Bytes,"softShare":packed.Budget.SoftShare,"maxBody":packed.Budget.MaxBody}})
}

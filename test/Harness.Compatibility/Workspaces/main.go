// Contract driver only; workspace storage, resolution, search and impact run in frozen Go.
package main

import (
    "context"
    "encoding/json"
    "os"

    "harness/internal/config"
    "harness/internal/machine"
    "harness/internal/providers"
    "harness/internal/workspace"
    "harness/internal/wsearch"
)

type fixture struct { ID string; Answers map[string][]providers.Item }
func (p *fixture) Name() string { return p.ID }
func (p *fixture) Capabilities() []providers.CapabilityInfo {
    out:=[]providers.CapabilityInfo{}
    for _,c:=range []providers.Capability{providers.SymbolSearch,providers.References,providers.Impact,providers.TextSearch} {
        out=append(out,providers.CapabilityInfo{Capability:c,Provider:p.ID,Evidence:providers.Evidence("exact")})
    }
    return out
}
func (p *fixture) Health(context.Context,bool) providers.Health { return providers.Health{} }
func (p *fixture) Close() error { return nil }
func (p *fixture) Do(_ context.Context, req providers.Request)(providers.Result,error) {
    items:=append([]providers.Item{},p.Answers[string(req.Capability)+"|"+req.Query]...)
    for i:=range items { items[i].Provider=p.ID }
    return providers.Result{Items:items},nil
}
func registry(p *fixture) *providers.Registry { r:=providers.NewRegistry(0);r.Register(p);return r }
func list[T any](s []T) []T { if s==nil {return []T{}};return s }
func must(err error) { if err!=nil {panic(err)} }
func main() {
    var input struct {
        Mode string
        Global string
        Current workspace.Member
        Members []workspace.Member
        Projects []machine.Project
        Fixtures map[string]*fixture
        Refs []string
        Query string
        Only string
        Deep bool
    }
    must(json.NewDecoder(os.Stdin).Decode(&input))
    must(os.Setenv("HARNESS_CONFIG_DIR",input.Global))
    output:=map[string]any{}
    if input.Mode=="management" {
        w,err:=workspace.Create("  EXAMPLE  ");must(err)
        added,err:=workspace.Join(w.Name,"b","a","b","missing");must(err)
        duplicate,err:=workspace.Join(w.Name,"a");must(err)
        title:=" Friendly\n workspace 🧪 "
        renamed,err:=workspace.Rename(w.Name," Renamed ",&title);must(err)
        left,err:=workspace.Leave(renamed.Name,"missing");must(err)
        absent,err:=workspace.Leave(renamed.Name,"missing");must(err)
        f,err:=workspace.Load();must(err)
        members:=workspace.MembersOf("a",f,machine.Registry{Projects:input.Projects})
        refs:=[]any{}
        for _,s:=range input.Refs { r,ok:=workspace.ParseRef(s,members); refs=append(refs,map[string]any{"ok":ok,"project":r.Project,"path":r.Path,"symbol":r.Symbol}) }
        output=map[string]any{"name":renamed.Name,"title":renamed.Title,"same_id":w.ID==renamed.ID,"members":f.Workspaces[0].Members,"added":list(added),"duplicate":list(duplicate),"left":left,"absent":absent,"resolved":list(members),"refs":refs}
        must(workspace.Delete(renamed.Name))
        f,err=workspace.Load();must(err);output["remaining"]=len(f.Workspaces)
    } else {
        wsearch.Build=func(_ config.Config,root string,_ bool)*providers.Registry {return registry(input.Fixtures[root])}
        current:=registry(input.Fixtures[input.Current.Root]);defer current.Close()
        search:=wsearch.New(input.Current,input.Members,current);defer search.Close()
        hit:=func(h wsearch.Hit)map[string]any{return map[string]any{"project":h.Member.Name,"id":h.Member.ID,"item":h.Item,"evidence":h.Evidence}}
        hits:=[]any{}
        if input.Mode=="impact" {
            answer:=search.Impact(context.Background(),input.Query,10,input.Deep)
            for _,h:=range answer.Hits {row:=hit(h.Hit);row["via"]=h.Via;row["exact"]=h.Exact;hits=append(hits,row)}
            output=map[string]any{"home":answer.Home,"hits":hits,"notes":list(answer.Notes),"errors":list(answer.Errors)}
        } else {
            answer:=search.Query(context.Background(),providers.Request{Capability:providers.SymbolSearch,Query:input.Query,Limit:10},input.Deep,input.Only)
            for _,h:=range answer.Hits {hits=append(hits,hit(h))}
            output=map[string]any{"hits":hits,"notes":list(answer.Notes),"errors":list(answer.Errors)}
        }
    }
    must(json.NewEncoder(os.Stdout).Encode(output))
}

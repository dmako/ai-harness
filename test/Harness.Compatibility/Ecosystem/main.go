// Contract driver: all algorithms are the unmodified frozen source implementation.
package main

import (
    "context"
    "encoding/json"
    "os"
    "path/filepath"

    "harness/internal/config"
    "harness/internal/archread"
    "harness/internal/dotnet"
    "harness/internal/ecosystem"
    "harness/internal/providers"
    "harness/internal/readcache"
    "harness/internal/stack"
    "harness/internal/workspace"
    "harness/internal/wsearch"
)

type fixture struct { ID string; Items []providers.Item }
func (p *fixture) Name() string {return p.ID}
func (p *fixture) Capabilities() []providers.CapabilityInfo {return []providers.CapabilityInfo{{Capability:providers.SymbolSearch,Provider:p.ID}}}
func (p *fixture) Health(context.Context,bool) providers.Health {return providers.Health{}}
func (p *fixture) Close() error {return nil}
func (p *fixture) Do(_ context.Context,_ providers.Request)(providers.Result,error){return providers.Result{Items:p.Items},nil}
func registry(p *fixture)*providers.Registry {r:=providers.NewRegistry(0);r.Register(p);return r}
func list[T any](s []T)[]T {if s==nil{return []T{}};return s}
func main(){
    var input struct { Mode,Root,Project,Library,Concern,Global string; Files []string; Members []workspace.Member; Providers map[string]*fixture }
    must(json.NewDecoder(os.Stdin).Decode(&input))
    ctx:=readcache.With(context.Background())
    var output any
    switch input.Mode {
    case "evaluate":
        ev:=dotnet.Evaluate(input.Root,filepath.Join(input.Root,filepath.FromSlash(input.Project)))
        for i:=range ev.Packages {ev.Packages[i].Frameworks=list(ev.Packages[i].Frameworks)}
        for i:=range ev.References {ev.References[i].Frameworks=list(ev.References[i].Frameworks)}
        output=map[string]any{"frameworks":list(ev.Frameworks),"properties":ev.Props,"locations":ev.PropAt,"packages":list(ev.Packages),"references":list(ev.References)}
    case "build":
        b,ok:=archread.Read(input.Root,input.Files);if ok{output=b}else{output=nil}
    case "stack":
        s,ok:=stack.Read(input.Root,input.Files);if ok{output=s}else{output=nil}
    case "ecosystem":
        out:=[]any{}
        for _,f:=range input.Files {if e:=ecosystem.Of(f);e!=nil {name,published:=e.Package(input.Root,f);out=append(out,map[string]any{"path":f,"ecosystem":e.Name(),"name":name,"published":published,"dependencies":list(e.Dependencies(input.Root,f))})}}
        output=out
    case "adoption":
        report,err:=wsearch.Adoption(ctx,input.Members,input.Library);must(err);output=report
    case "dependencies":
        report,err:=wsearch.Dependencies(ctx,input.Members);must(err);output=report
    case "concern":
        must(os.Setenv("HARNESS_CONFIG_DIR",input.Global))
        wsearch.Build=func(_ config.Config,root string,_ bool)*providers.Registry{return registry(input.Providers[root])}
        current:=registry(input.Providers[input.Members[0].Root]);defer current.Close()
        search:=wsearch.New(input.Members[0],input.Members,current);defer search.Close()
        output=search.Concern(ctx,input.Concern,false)
    }
    must(json.NewEncoder(os.Stdout).Encode(output))
}
func must(err error){if err!=nil{panic(err)}}

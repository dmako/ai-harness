// Thin contract driver around the unmodified pinned implementation.
package main

import (
 "context"
 "encoding/json"
 "os"
 "reflect"
 "time"
 "harness/internal/clones"
 "harness/internal/findings"
 "harness/internal/arch"
 "harness/internal/coupling"
 "harness/internal/providers"
 "harness/internal/git"
 "harness/internal/wsearch"
 "harness/internal/workspace"
)

type fixture struct {ID string; Table map[string][]providers.Item; Error string; Capability providers.Capability}
func(p fixture) Name()string{return p.ID}
func(p fixture) Capabilities()[]providers.CapabilityInfo{return []providers.CapabilityInfo{{Capability:p.Capability,Provider:p.ID}}}
func(p fixture) Health(context.Context,bool)providers.Health{return providers.Health{}}
func(p fixture) Close()error{return nil}
func(p fixture) Do(_ context.Context,r providers.Request)(providers.Result,error){items:=p.Table[r.Query];for i:=range items{items[i].Provider=p.ID;items[i].Tool="fixture"};return providers.Result{Items:items},nil}
func list[T any](items []T)[]T{if items==nil{return []T{}};return items}
func stringsOf(f clones.Func,field string)[]string{v:=reflect.ValueOf(f).FieldByName(field);out:=[]string{};for i:=0;i<v.Len();i++{out=append(out,v.Index(i).String())};return out}
func statementsOf(f clones.Func)[]any {
 v:=reflect.ValueOf(f).FieldByName("units");out:=[]any{}
 for i:=0;i<v.Len();i++{u:=v.Index(i);strs:=func(name string)[]string{v:=u.FieldByName(name);r:=[]string{};for j:=0;j<v.Len();j++{r=append(r,v.Index(j).String())};return r}
 out=append(out,map[string]any{"key":u.FieldByName("key").String(),"own":strs("own"),"raw":strs("raw"),"line":u.FieldByName("line").Int(),"size":u.FieldByName("size").Int(),"idiom":u.FieldByName("idiom").Bool(),"header":u.FieldByName("header").Bool(),"work":u.FieldByName("work").Bool(),"callee":u.FieldByName("callee").String(),"data":u.FieldByName("data").Bool()})
 };return out
}
func main(){
 var in struct {Mode,Root,File string;Files,Changed []string;Since time.Time;Providers []fixture; Members []workspace.Member; Global,Workspace string; Library map[string]bool;Provided []arch.Smell;Limit,Partners int}
 must(json.NewDecoder(os.Stdin).Decode(&in))
 ctx:=context.Background();var out any
 reg:=providers.NewRegistry(0);defer reg.Close();for _,p:=range in.Providers{reg.Register(p)}
 switch in.Mode {
 case "read":
  fs,branches:=clones.ExtractAll(in.Root,in.Files);copies:=clones.Find(fs,clones.Defaults);templates:=clones.Templates(fs,clones.Defaults)
  out=map[string]any{"functions":list(fs),"branches":list(branches),"copies":list(copies),"templates":list(templates),"sequences":list(clones.Sequences(fs,copies,templates,clones.SeqDefaults)),"contracts":list(clones.Contracts(fs,branches,clones.Defaults))}
 case "workspace":d,err:=wsearch.FindDuplicates(ctx,in.Members);must(err);out=d
 case "shared":must(os.Setenv("HARNESS_CONFIG_DIR",in.Global));d,ss,err:=findings.SharedCode(ctx,in.Workspace,in.Members,in.Library);must(err);out=map[string]any{"report":d,"findings":list(ss)}
 case "forms":
  fs:=clones.Extract(in.Root,in.Files);forms:=[]any{};for _,f:=range fs{forms=append(forms,map[string]any{"function":f,"canonical":stringsOf(f,"canon"),"shape":stringsOf(f,"shape"),"signature":stringsOf(f,"sig"),"statements":statementsOf(f)})};out=forms
 case "findings":out=list(findings.RepeatedSmells(in.Root,in.Files,in.Provided))
 case "partners": ps,errs:=coupling.Partners(ctx,reg,in.File,in.Limit);out=map[string]any{"partners":list(ps),"errors":list(errs)}
 case "check": missing,errs:=coupling.Check(ctx,reg,in.Root,in.Changed);out=map[string]any{"missing":list(missing),"errors":list(errs)}
 case "hotspots":r,err:=git.Discover(in.Root);must(err);hot,commits,errs,err:=coupling.Hotspots(ctx,r,reg,in.Limit,in.Partners);must(err);out=map[string]any{"hotspots":list(hot),"commits":commits,"errors":list(errs)}
 case "churn":r,err:=git.Discover(in.Root);must(err);counts,commits,err:=r.Churn(ctx,in.Since);must(err);out=map[string]any{"counts":counts,"commits":commits}
 }
 must(json.NewEncoder(os.Stdout).Encode(out))
}
func must(err error){if err!=nil{panic(err)}}

// All analysis is delegated to the pinned original implementation.
package main

import (
    "encoding/json"
    "os"
    "harness/internal/arch"
    "harness/internal/archread"
    "harness/internal/findings"
    "harness/internal/system"
    "harness/internal/config"
)

func main() {
    var input struct {
        Mode, Root string
        Files []string
        Used *[]string
        Knowledge []struct {Text string; Ref arch.RuleRef}
        Members []system.Member
        Evidence string
        Span,Max int
        Config config.Config
    }
    must(json.NewDecoder(os.Stdin).Decode(&input))
    var out any
    switch input.Mode {
    case "analyze":
        b,ok:=archread.Read(input.Root,input.Files)
        if !ok {break}
        var used func(string)bool
        if input.Used!=nil {used=func(p string)bool{for _,u:=range *input.Used{if u==p{return true}};return false}}
        a:=arch.Analyze(b,used)
        var rules arch.RuleSet
        for _,m:=range input.Knowledge {
            groups,rs,errs:=arch.ParseRules(m.Text,m.Ref)
            for _,g:=range groups {exists:=false;for _,old:=range rules.Groups{if old.Name==g.Name{exists=true}};if !exists{rules.Groups=append(rules.Groups,g)}}
            rules.Rules=append(rules.Rules,rs...);rules.Errors=append(rules.Errors,errs...)
        }
        if rules.Overrides(b.Units){filtered:=a.Smells[:0];for _,f:=range a.Smells{if f.Kind!="layering"{filtered=append(filtered,f)}};a.Smells=filtered}
        a.Smells=append(a.Smells,arch.Keyed(rules.Check(b.Units))...)
        out=a
    case "system": out=system.Read(input.Members)
    case "coverage": out=findings.CoverageFromConfig(input.Config)
    case "site": site,ok:=findings.ParseSite(input.Evidence);if ok{out=site}
    case "snippet":
        site,ok:=findings.ParseSite(input.Evidence);if !ok{panic("no site")}
        snippet,err:=findings.ReadSnippet(input.Root,site,input.Span,input.Max);must(err);out=snippet
    }
    must(json.NewEncoder(os.Stdout).Encode(out))
}
func must(err error){if err!=nil{panic(err)}}

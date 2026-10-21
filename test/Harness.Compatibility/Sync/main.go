// Drives the frozen implementation; timestamps/opaque IDs are intentionally not comparison fields.
package main

import (
    "context"
    "encoding/json"
    "os"
    "harness/internal/git"
    "harness/internal/gitsync"
    "harness/internal/project"
    "harness/internal/storage"
)

func main() {
    var in struct { Root, Database, Mode, Since string; Ignore []string }
    if err:=json.NewDecoder(os.Stdin).Decode(&in); err!=nil { panic(err) }
    ctx:=context.Background()
    repo,err:=git.Discover(in.Root); if err!=nil { panic(err) }
    db,err:=storage.Open(ctx,in.Database,storage.Options{}); if err!=nil { panic(err) }; defer db.Close()
    reg,err:=project.Register(ctx,db,repo); if err!=nil { panic(err) }
    r,err:=gitsync.Run(ctx,db,repo,reg.Worktree.ID,gitsync.Options{Mode:in.Mode,Since:in.Since,Ignore:in.Ignore}); if err!=nil { panic(err) }
    hashes,err:=gitsync.FileHashes(ctx,db,reg.Worktree.ID); if err!=nil { panic(err) }
    cs:=[]map[string]string{}
    for _,c:=range r.Changes { cs=append(cs,map[string]string{"kind":c.Kind,"path":c.Path,"old_path":c.OldPath}) }
    paths:=func(p []string) []string { if p==nil {return []string{}}; return p }
    out:=map[string]any{"mode":r.Mode,"reason":r.Reason,"base":r.Base,"head":r.Head,"branch_switch":r.BranchSwitch,
      "changes":cs,"files":map[string]any{"added":paths(r.Files.Added),"changed":paths(r.Files.Changed),"removed":paths(r.Files.Removed),"unchanged":r.Files.Unchanged,"hashed":r.Files.Hashed},
      "state":map[string]any{"branch":r.State.Branch,"commit":r.State.Commit,"detached":r.State.Detached,"dirty":r.State.Dirty,"dirty_paths":r.State.DirtyPaths,"pending":paths(r.State.Pending),"providers_stale":r.State.ProvidersStale},"hashes":hashes}
    if err=json.NewEncoder(os.Stdout).Encode(out);err!=nil { panic(err) }
}

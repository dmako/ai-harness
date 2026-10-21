// Thin differential driver: algorithms and persistence are the frozen Go implementation.
package main

import (
	"context"
	"encoding/json"
	"harness/internal/arch"
	"harness/internal/findings"
	"harness/internal/storage"
	"os"
)

func main() {
	var in struct {
		DB, Mode, Commit, Key, Root  string
		Files                        []string
		Found                        []arch.Smell
		Mark                         findings.Mark
		Proposal                     findings.Proposal
		ID, Status, By, Note, Result string
	}
	must(json.NewDecoder(os.Stdin).Decode(&in))
	ctx := context.Background()
	db, err := storage.Open(ctx, in.DB, storage.Options{})
	must(err)
	defer db.Close()
	s := findings.NewStore(db)
	var out any
	switch in.Mode {
	case "drift":
		out = findings.Stale(ctx, db, findings.ProjectRead{Root: in.Root, Files: in.Files})
	case "take":
		r, e := s.Take(ctx, in.Commit, in.Found)
		must(e)
		out = r
	case "annotate":
		f, snap, e := s.Annotate(ctx, in.Found)
		must(e)
		out = map[string]any{"findings": f, "snapshot": snap}
	case "current":
		f, snap, e := s.Current(ctx)
		must(e)
		out = map[string]any{"findings": f, "snapshot": snap}
	case "mark":
		f, ok := findings.Find(in.Found, in.Key)
		if !ok {
			panic("no finding")
		}
		m, e := s.Put(ctx, f, in.Mark)
		must(e)
		out = m
	case "remove":
		m, e := s.Remove(ctx, in.Key)
		must(e)
		out = m
	case "propose":
		p, exists, e := s.Propose(ctx, in.Proposal)
		must(e)
		out = map[string]any{"proposal": p, "existing": exists}
	case "decide":
		p, e := s.Decide(ctx, in.ID, in.Status, in.By, in.Note, in.Result)
		must(e)
		out = p
	case "proposals":
		p, e := s.Proposals(ctx, in.Status)
		must(e)
		out = p
	}
	must(json.NewEncoder(os.Stdout).Encode(out))
}
func must(e error) {
	if e != nil {
		panic(e)
	}
}

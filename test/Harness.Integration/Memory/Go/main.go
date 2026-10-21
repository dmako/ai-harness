// Test-only bridge to the unmodified memory package at the frozen Go commit.
package main

import (
    "context"
    "encoding/json"
    "os"
    "path/filepath"
    "time"
    "unicode"

    "harness/internal/memory"
    "harness/internal/redact"
    "harness/internal/storage"
)

var ctx = context.Background()

func must[T any](value T, err error) T {
    if err != nil { panic(err) }
    return value
}

func put(s *memory.Store, content, scope string) memory.Memory {
    r := must(s.Put(ctx, memory.PutInput{Type: memory.Decision, Content: content,
        Scope: must(memory.ParseScope(scope)), Source: memory.Source{Type: memory.SourceUser, Ref: "Go legacy fixture", By: "go-oracle"}, Force: true}))
    return *r.Memory
}

func snapshot(m memory.Memory) map[string]any {
    var from, until any
    if m.ValidFrom != nil { from = storage.Time(*m.ValidFrom) }
    if m.ValidUntil != nil { until = storage.Time(*m.ValidUntil) }
    return map[string]any{"id": m.ID, "type": string(m.Type), "content": m.Content, "scope": m.Scope.String(),
        "source_type": m.Source.Type, "source_ref": m.Source.Ref, "recorded_by": m.Source.By,
        "confidence": m.Confidence, "superseded_by": m.SupersededBy, "supersede_reason": m.SupersedeReason,
        "valid_from": from, "valid_until": until, "created_at": storage.Time(m.CreatedAt), "updated_at": storage.Time(m.UpdatedAt), "overrides": m.Overrides}
}

func snapshots(ms []memory.Memory) []any {
    out := []any{}
    for _, m := range ms { out = append(out, snapshot(m)) }
    return out
}

func report(s *memory.Store) map[string]any {
    path := must(memory.ParseScope("path:src/api/handler.cs"))
    queries := []memory.Query{
        {}, {Scope: &path, Branch: "feature", Limit: 3}, {Scope: &path, Branch: "main"},
        {Text: "migration storage"}, {Text: "migration", Scope: &path}, {Text: "!!!"},
        {Types: []memory.Type{memory.Decision, memory.Warning}, IncludeInactive: true, Limit: 100},
        {Text: `"; DROP TABLE memories; --`},
    }
    searches := []any{}
    for _, q := range queries { searches = append(searches, snapshots(must(s.Search(ctx, q)))) }
    all := must(s.Search(ctx, memory.Query{IncludeInactive: true, Limit: 100}))
    histories, events := map[string]any{}, map[string]any{}
    for _, m := range all {
        histories[m.ID] = snapshots(must(s.History(ctx, m.ID)))
        es := must(s.Events(ctx, m.ID))
        list := []any{}
        for _, e := range es { list = append(list, map[string]any{"event": e.Event, "related_id": e.RelatedID, "actor": e.Actor, "detail": e.Detail, "at": storage.Time(e.At)}) }
        events[m.ID] = list
    }
    sims := []any{}
    for _, sim := range must(s.Similar(ctx, memory.Decision, "Store project memory in the SQLite project database")) {
        sims = append(sims, map[string]any{"memory": snapshot(sim.Memory), "similarity": sim.Similarity})
    }
    return map[string]any{"all": snapshots(all), "searches": searches, "histories": histories, "events": events, "similar": sims}
}

func main() {
    if os.Args[1] == "lower" || os.Args[1] == "unicode" {
        mapping := map[string]string{}
        for r := rune(0); r <= unicode.MaxRune; r++ {
            if lower := unicode.ToLower(r); lower != r { mapping[string(r)] = string(lower) }
        }
        var output any = mapping
        if os.Args[1] == "unicode" {
            ranges := [][2]int{}
            first := -1
            for r := rune(0); r <= unicode.MaxRune+1; r++ {
                if unicode.IsLetter(r) || unicode.IsDigit(r) {
                    if first == -1 { first = int(r) }
                } else if first != -1 {
                    ranges = append(ranges, [2]int{first, int(r)-1})
                    first = -1
                }
            }
            output = map[string]any{"version": unicode.Version, "lower": mapping, "word_ranges": ranges}
        }
        if err := json.NewEncoder(os.Stdout).Encode(output); err != nil { panic(err) }
        return
    }
    if os.Args[1] == "redaction" {
        cases := []string{
            "clone https://zaki:hunter2pass@gitlab.com/x.git please", "curl -H 'Authorization: Bearer abcdefghijklmnop1234567890'",
            "use key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123 for the call", "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123 leaked",
            "glpat-abcdefghijklmnopqrstu in the CI", "aws AKIAIOSFODNN7EXAMPLE key", "DB_PASSWORD=s3cr3t! and API_KEY: 'abc def'",
            `{"client_secret": "xyz"}`, "jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
            "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----",
            "İghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123İ", "token\vabcdefghijklmnop1234567890", "token\u00a0abcdefghijklmnop1234567890",
            "Fix OrderService.Confirm: stale Revision after PUT", "tokenizer splits task words; password reset page shows the wrong copy",
            "get_context budget=6000 path=internal/retrieval", "sk-learn style model", "no secrets here",
        }
        output := map[string]string{}
        for _, text := range cases { output[text] = redact.String(text) }
        if err := json.NewEncoder(os.Stdout).Encode(output); err != nil { panic(err) }
        return
    }
    root := os.Args[2]
    if os.Args[1] == "layers" {
        stores := []*memory.Store{}
        for _, name := range []string{"project", "workspace", "user"} {
            db := must(storage.Open(ctx, filepath.Join(root, name+".db"), storage.Options{}))
            defer db.Close()
            stores = append(stores, memory.NewStore(db))
        }
        put(stores[2], "Store project memory in the SQLite project database.", "project")
        put(stores[1], "Project memory is stored in the project SQLite database", "project")
        put(stores[0], "Store project memory in the SQLite project database.", "branch:other")
        expired := time.Date(2000, 1, 1, 0, 0, 0, 0, time.UTC)
        future := time.Date(2099, 1, 1, 0, 0, 0, 0, time.UTC)
        must(stores[0].Put(ctx, memory.PutInput{Type: memory.Warning, Content: "Old deployment warning", ValidUntil: &expired}))
        must(stores[1].Put(ctx, memory.PutInput{Type: memory.Warning, Content: "Future workspace warning", ValidFrom: &future}))
        put(stores[2], "User prefers deterministic logs", "project")
        layers := memory.Layers{Project: stores[0], Wider: []memory.Level{{Scope: must(memory.ParseScope("workspace:home")), Store: stores[1]}, {Scope: memory.UserScope, Store: stores[2]}}}
        target := must(memory.ParseScope("path:src/api/handler.cs"))
        out := []any{}
        for _, q := range []memory.Query{{}, {Scope: &target, Branch: "main"}, {IncludeInactive: true}, {Text: "SQLite"}, {Scope: &target, Limit: 1}} {
            out = append(out, snapshots(must(layers.Search(ctx, q))))
        }
        if err := json.NewEncoder(os.Stdout).Encode(out); err != nil { panic(err) }
        return
    }
    db := must(storage.Open(ctx, root, storage.Options{}))
    defer db.Close()
    s := memory.NewStore(db)
    if os.Args[1] == "seed" {
        a := put(s, "Use JSON for config.", "project")
        b := must(s.Supersede(ctx, a.ID, memory.PutInput{Content: "Use TOML for config.", SupersedeReason: "comments needed", Source: memory.Source{By: "go-reviewer", Ref: "Go review"}}))
        must(s.Supersede(ctx, b.Memory.ID, memory.PutInput{Content: "Use TOML for config.", SupersedeReason: "confirmed", Source: memory.Source{By: "go-reviewer"}}))
        put(s, "Migrations are embedded SQL files", "path:src")
        put(s, "Storage migration backup belongs here", "path:src/api")
        put(s, "Other migration files", "path:other")
        put(s, "On feature commit per task", "branch:feature")
        put(s, "Subsystem orders use transactions", "subsystem:orders")
        until := time.Date(2000, 1, 1, 0, 0, 0, 123456789, time.UTC)
        from := time.Date(2099, 1, 1, 0, 0, 0, 987654321, time.UTC)
        must(s.Put(ctx, memory.PutInput{Type: memory.Warning, Content: "Expired release freeze", ValidUntil: &until}))
        must(s.Put(ctx, memory.PutInput{Type: memory.Warning, Content: "Future release policy", ValidFrom: &from}))
        put(s, "Store project memory in the SQLite project database.", "project")
        put(s, "Project memory is stored in the project SQLite database", "path:src")
        r := put(s, "Retired historical memory", "project")
        must(s.Retire(ctx, r.ID, "human:go", "no longer applies"))
        must(s.Put(ctx, memory.PutInput{Type: memory.Fact, Content: "İSTANBUL 𐐀 Á Ž Σ K Unicode compatibility"}))
    }
    if err := json.NewEncoder(os.Stdout).Encode(report(s)); err != nil { panic(err) }
}

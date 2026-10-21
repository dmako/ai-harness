// Thin driver around the frozen telemetry and report algorithms.
package main

import (
	"context"
	"encoding/json"
	"harness/internal/activity"
	"harness/internal/git"
	"harness/internal/report"
	"harness/internal/storage"
	"harness/internal/telemetry"
	"os"
	"time"
)

func main() {
	var in struct {
		Mode, DB, Root, Worktree, Head, By string
		Since, Now                         time.Time
		Days                               int
		Servers                            map[string]string
		Report                             report.Report
	}
	must(json.NewDecoder(os.Stdin).Decode(&in))
	ctx := context.Background()
	var out any
	if in.Mode == "findings" {
		out = report.Findings(in.Report, in.Now)
	} else {
		db, e := storage.Open(ctx, in.DB, storage.Options{})
		must(e)
		defer db.Close()
		switch in.Mode {
		case "activity":
			out, e = activity.Read(ctx, db, in.Since, 20)
		case "metrics":
			out, e = telemetry.ReportMetrics(ctx, db, in.Since)
		case "stats":
			out, e = telemetry.Report(ctx, db, in.Since)
		case "trends":
			out, e = telemetry.ProviderTrends(ctx, db, in.Since, in.Now, 3000)
		case "build":
			out, e = report.Build(ctx, db, in.Days, in.Servers, in.Now)
		case "bypasses":
			out, e = telemetry.Bypasses(ctx, db, in.Since, in.Servers)
		case "resolve":
			var repo *git.Repo
			repo, e = git.Discover(in.Root)
			must(e)
			out, e = telemetry.Resolve(ctx, db, repo, in.Worktree, in.Head, in.By)
		}
		must(e)
	}
	must(json.NewEncoder(os.Stdout).Encode(out))
}
func must(e error) {
	if e != nil {
		panic(e)
	}
}

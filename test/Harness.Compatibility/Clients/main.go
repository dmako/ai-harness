package main

import (
	"encoding/json"
	"fmt"
	"harness/internal/clients"
	"harness/internal/instructions"
	"os"
)

type input struct {
	Mode, Client, Scope, Root, ClaudeDir, CodexDir, Command string
	Args                                                    []string
	Codex                                                   bool
}
type change struct {
	Path, Action, Before, After string
	Deleted                     bool
}

func main() {
	var in input
	if err := json.NewDecoder(os.Stdin).Decode(&in); err != nil {
		panic(err)
	}
	os.Setenv("CLAUDE_CONFIG_DIR", in.ClaudeDir)
	os.Setenv("CODEX_HOME", in.CodexDir)
	var result any
	var err error
	switch in.Mode {
	case "section":
		result = instructions.Section()
	case "instructions", "remove-instructions":
		var changes []instructions.Change
		if in.Mode == "instructions" {
			changes, err = instructions.Project(in.Root, in.Codex, true)
		} else {
			changes, err = instructions.RemoveProject(in.Root, true)
		}
		out := []change{}
		for _, c := range changes {
			out = append(out, change{c.Path, c.Action, string(c.Before), string(c.After), c.Deleted})
		}
		result = out
	default:
		targets, e := clients.TargetsFor(in.Client, in.Scope, in.Root)
		err = e
		out := []change{}
		if err == nil {
			for _, t := range targets {
				var c clients.Change
				if in.Mode == "install" {
					c, err = clients.Install(t, clients.ServerSpec{Command: in.Command, Args: in.Args}, true)
				} else {
					c, err = clients.Uninstall(t, true)
				}
				if err != nil {
					break
				}
				out = append(out, change{t.Path, c.Action, string(c.Before), string(c.After), c.Deleted})
			}
		}
		result = out
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err = json.NewEncoder(os.Stdout).Encode(result); err != nil {
		panic(err)
	}
}

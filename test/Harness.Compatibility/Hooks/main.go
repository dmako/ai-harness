package main

import (
	"encoding/json"
	"fmt"
	"harness/internal/clients"
	"harness/internal/hooks"
	"os"
)

type input struct {
	Mode, Client, Scope, Root, ClaudeDir, CodexDir, Command string
	Payload                                                 json.RawMessage
}
type change struct {
	Path, Action, Before, After, Reason string
	Deleted                             bool
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
	case "payload":
		result, err = clients.ParseHookPayload(in.Payload)
	case "command":
		result = clients.HookCommand(in.Command, in.Client)
	case "git-install", "git-remove":
		var plan hooks.Plan
		if in.Mode == "git-install" {
			plan, err = hooks.Install(in.Root, in.Command, true)
		} else {
			plan, err = hooks.Uninstall(in.Root, true)
		}
		out := []change{}
		for _, c := range plan.Changes {
			out = append(out, change{c.Path, c.Action, string(c.Before), string(c.After), c.Reason, c.Deleted})
		}
		result = out
	default:
		var target clients.Target
		target, err = clients.HookTargetsFor(in.Client, in.Scope, in.Root)
		if err == nil {
			var c clients.Change
			if in.Mode == "install" {
				c, err = clients.InstallHook(target, in.Command, true)
			} else {
				c, err = clients.UninstallHook(target, true)
			}
			result = change{target.Path, c.Action, string(c.Before), string(c.After), c.Reason, c.Deleted}
		}
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err = json.NewEncoder(os.Stdout).Encode(result); err != nil {
		panic(err)
	}
}

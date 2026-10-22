// Test-only additions to the frozen Go fresh-machine runner. Never shipped.
package main

import (
    "bufio"
    "bytes"
    "fmt"
    "io"
    "net/http"
    "os"
    "os/exec"
    "path/filepath"
    "regexp"
    "runtime"
    "strings"
    "syscall"
    "time"
)

func privateEnvironment(env []string, home string) []string {
    for _, name := range []string{".bundle", ".localappdata", ".appdata"} {
        must(os.MkdirAll(filepath.Join(home, name), 0700))
    }
    env = append(env, "DOTNET_BUNDLE_EXTRACT_BASE_DIR="+filepath.Join(home, ".bundle"),
        "DOTNET_ROOT="+filepath.Join(home, "no-runtime"), "DOTNET_MULTILEVEL_LOOKUP=0",
        "DOTNET_CLI_TELEMETRY_OPTOUT=1", "TEMP="+filepath.Join(home, ".tmp"),
        "TMP="+filepath.Join(home, ".tmp"), "USERPROFILE="+home,
        "LOCALAPPDATA="+filepath.Join(home, ".localappdata"), "APPDATA="+filepath.Join(home, ".appdata"))
    if root := os.Getenv("SystemRoot"); root != "" { env = append(env, "SystemRoot="+root) }
    return env
}

func protocolCommand(executable, repo string) *exec.Cmd {
    if strings.Contains(filepath.Base(executable), "graphify") {
        return exec.Command(executable, "--root", repo, "--graphify", filepath.Join(repo, "missing-optional-backend"))
    }
    return exec.Command(executable, "mcp", "--project", repo)
}

func releaseChecks(harness, bin, home, repo string, env []string, archive string, developer bool) {
    run := func(name string, args ...string) string {
        start := time.Now()
        cmd := exec.Command(harness, args...)
        cmd.Env, cmd.Dir = env, repo
        output, err := cmd.CombinedOutput()
        check(err == nil, name, err, string(output))
        fmt.Printf("MEASURE %s %.2f ms\n", name, float64(time.Since(start).Microseconds())/1000)
        return string(output)
    }
    check(strings.Contains(run("embedded docs", "docs", "guide"), "harness"), "documentation body present")
    configuration := filepath.Join(home, ".config", "harness", "config.toml")
    var packs strings.Builder
    for _, name := range []string{"codebase-memory", "graphify-csharp", "nuget", "rg-fd", "ripwire"} {
        fmt.Fprintf(&packs, "[providers.%s]\npack = %q\n", name, name)
    }
    must(os.WriteFile(configuration, []byte(packs.String()), 0600))
    run("five embedded provider packs", "init", "--dry-run", "--project", repo)
    must(os.Remove(configuration))
    run("warm version", "version")
    check(strings.Contains(run("provider sessions help", "providers", "sessions", "--help"), "list"), "provider CLI registered")
    // The sibling protocol adapter must start with no external semantic backend.
    results, err := mcpSession(filepath.Join(bin, "harness-graphify"+exeSuffix()), env, repo,
        []call{{"harness_provider_info", map[string]any{}}})
    check(err == nil && len(results) == 1 && strings.Contains(results[0], "harness-provider/1"), "packaged Graphify adapter discovery/handshake", err, results)
    sqlite := 0
    must(filepath.WalkDir(filepath.Join(home, ".bundle"), func(path string, entry os.DirEntry, err error) error {
        if err != nil { return err }
        if !entry.IsDir() && strings.Contains(entry.Name(), "e_sqlite3") { sqlite++ }
        return nil
    }))
    check(sqlite > 0, "native SQLite extracted from bundle", sqlite)
    if runtime.GOOS != "windows" {
        info, err := os.Stat(filepath.Join(home, ".bundle"))
        check(err == nil && info.Mode().Perm() == 0700, "native extraction private owner directory", err)
    } else { fmt.Println("NOTE Windows bundle ACL inherits the isolated current-user temporary directory; ACL audited by host runner") }
    ui, address := startService(harness, env, repo, "ui", "--no-open", "--addr", "127.0.0.1:0")
    if ui != nil {
        for _, route := range []string{"/", "/app.js", "/style.css", "/api/docs", "/api/docs/guide"} {
            status, body, headers := request("GET", address+route, "", "")
            check(status == 200 && len(body) > 20, "embedded HTTP "+route, status, body)
            check(headers.Get("X-Content-Type-Options") == "nosniff", "HTTP nosniff "+route)
        }
        stopService(ui, "UI")
    }
    if developer { companionChecks(harness, bin, home, repo, env, archive, run) }
}

var addressPattern = regexp.MustCompile(`http://127\.0\.0\.1:[0-9]+/?`)

func startService(executable string, env []string, repo string, args ...string) (*exec.Cmd, string) {
    cmd := exec.Command(executable, args...)
    cmd.Env, cmd.Dir = env, repo
    out, err := cmd.StdoutPipe()
    must(err)
    var errors bytes.Buffer
    cmd.Stderr = &errors
    if err = cmd.Start(); err != nil { check(false, "start "+filepath.Base(executable), err); return nil, "" }
    addresses := make(chan string, 1)
    go func() {
        scanner := bufio.NewScanner(out)
        for scanner.Scan() {
            if address := addressPattern.FindString(scanner.Text()); address != "" {
                select { case addresses <- strings.TrimSuffix(address, "/"): default: }
            }
        }
    }()
    select {
    case address := <-addresses: check(true, "start "+filepath.Base(executable)); return cmd, address
    case <-time.After(30*time.Second):
        cmd.Process.Kill(); cmd.Wait()
        check(false, "service start deadline "+filepath.Base(executable), errors.String()); return nil, ""
    }
}

func stopService(cmd *exec.Cmd, name string) {
    if runtime.GOOS == "windows" {
        cmd.Process.Kill(); cmd.Wait()
        fmt.Println("NOTE", name, "Windows forced cleanup; console Ctrl-C requires a native console runner")
        return
    }
    err := cmd.Process.Signal(syscall.Signal(15))
    done := make(chan error, 1)
    go func() { done <- cmd.Wait() }()
    select {
    case result := <-done: check(err == nil && result == nil, name+" SIGTERM graceful exit", err, result)
    case <-time.After(15*time.Second): cmd.Process.Kill(); <-done; check(false, name+" SIGTERM deadline")
    }
}

func request(method, url, body, token string) (int, string, http.Header) {
    req, err := http.NewRequest(method, url, strings.NewReader(body)); must(err)
    if token != "" { req.Header.Set("Authorization", "Bearer "+token) }
    client := &http.Client{Timeout: 10*time.Second}
    response, err := client.Do(req)
    if err != nil { check(false, "HTTP request "+url, err); return 0, "", http.Header{} }
    defer response.Body.Close()
    bytes, err := io.ReadAll(io.LimitReader(response.Body, 1<<20)); must(err)
    return response.StatusCode, string(bytes), response.Header
}

func companionChecks(harness, bin, home, repo string, env []string, archive string, run func(string, ...string) string) {
    collector, address := startService(filepath.Join(bin, "harness-collect"+exeSuffix()), env, repo,
        "--dir", filepath.Join(home, "reports"), "--addr", "127.0.0.1:0", "--token", "probe-token")
    if collector != nil {
        status, _, _ := request("POST", address+"/v1/report", `{}`, "")
        check(status == 401, "collector authentication", status)
        status, _, _ = request("POST", address+"/v1/report", `{}`, "probe-token")
        check(status == 400, "collector schema rejection", status)
        run("telemetry enable", "telemetry", "enable", "--endpoint", address+"/v1/report", "--token", "probe-token")
        preview := run("telemetry preview", "telemetry", "preview")
        check(!strings.Contains(preview, home) && !strings.Contains(preview, "ConfirmOrder"), "export omits private paths/content")
        run("telemetry send", "telemetry", "send")
        run("telemetry disable", "telemetry", "disable")
        state := filepath.Join(home, ".config", "harness", "telemetry.json")
        if runtime.GOOS != "windows" {
            info, err := os.Stat(state)
            check(err == nil && info.Mode().Perm() == 0600, "telemetry token owner-only permissions", err)
        }
        stopService(collector, "collector")
    }
    share, address := startService(filepath.Join(bin, "harness-share"+exeSuffix()), env, repo,
        "--dir", filepath.Dir(archive), "--addr", "127.0.0.1:0")
    if share != nil {
        status, _, _ := request("HEAD", address+"/download/"+filepath.Base(archive), "", "")
        check(status == 200, "share packaged archive HEAD", status)
        status, _, _ = request("POST", address+"/", "", "")
        check(status == 405, "share rejects writes", status)
        status, _, _ = request("GET", address+"/download/%2e%2e/NOTICE.md", "", "")
        check(status == 404, "share path allowlist", status)
        stopService(share, "share")
    }
}

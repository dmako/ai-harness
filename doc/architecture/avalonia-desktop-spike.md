# Avalonia desktop host

`Harness.Ui.Desktop` runs the shared Avalonia UI in a native window. Start `harness ui --no-open` first, then run `dotnet run --project src/Harness.Ui.Desktop`. The host connects to `http://127.0.0.1:17477` by default. Like `harness ui`, it uses `HARNESS_UI_ADDR` when set. Pass an address as the first argument to override either default, for example `dotnet run --project src/Harness.Ui.Desktop -- 127.0.0.1:17478`. The address is configured rather than discovered by scanning ports; an instance started with `--addr` must be supplied explicitly or via the environment.

The desktop process talks to the running server's HTTP API and does not start a server. Its navigation and preferences currently live in memory for the lifetime of the window. Links that open a new page open in the browser.

To try a standalone native publish on a matching host, run `dotnet publish src/Harness.Ui.Desktop -c Release -r win-x64 -p:PublishAot=true`. The supported runtime identifiers are `win-x64`, `linux-x64`, `linux-arm64` and `osx-arm64`; Native AOT requires the native toolchain for the target platform. The desktop publish is optional and separate from the existing harness release archive.

The spike published on Windows x64 and Linux x64 (WSL). Windows Native AOT reported two Avalonia DirectComposition methods with non-blittable parameters that will throw if invoked; startup and initial API requests succeeded, but those rendering paths need further qualification. Linux ARM64 and macOS ARM64 have not been tested. A publish with `PublishAot=true` changes the generated NuGet lock-file inputs; restore the normal solution lock files before committing them.

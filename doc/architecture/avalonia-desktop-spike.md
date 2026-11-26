# Avalonia desktop host

`Harness.Ui.Desktop` runs the shared Avalonia UI in a native window. The release publishes it as `harness-ui-app` (`harness-ui-app.exe` on Windows), a windowed app without a console.

## Connecting to harness ui

The host connects to `http://127.0.0.1:17477` by default. Like `harness ui`, it uses `HARNESS_UI_ADDR` when set. To override either default, pass an address as the first argument, for example `harness-ui-app 127.0.0.1:17478` or `dotnet run --project src/Harness.Ui.Desktop -- 127.0.0.1:17478`. The address is configured rather than discovered by scanning ports.

If a harness ui already answers on the address, the app uses it. If nothing answers and the address is local, the app starts `harness ui --no-open --addr <address>` itself (TASK-0214) and stops it when its window closes. It runs the `harness` beside its own executable, or else the first one on PATH.

The window's navigation and preferences live in memory for as long as the window is open. Links that open a new page open in the browser.

The native window uses the shared application logo; Windows executables also embed it as an ICO. A macOS app bundle or Linux desktop launcher would need its own platform packaging and icon metadata, if those packages are ever produced.

## Release

Every release ships the app as its own archive, `ui-app/harness-ui-app-VERSION-OS-ARCH` (TASK-0219; see [releases](../releases.md)). The archive is published as trimmed Native AOT for `win-x64`, `linux-x64`, `linux-arm64` and `osx-arm64`. Each runtime is built on its own host, like the CLI tools, and `src/Harness.Ui.Desktop/packages.publish.lock.json` locks that publish graph.

Native AOT on Windows reports two Avalonia DirectComposition methods with non-blittable parameters that "will always throw". Both are MicroCom vtable entries for *implementing* `IDCompositionDevice` and `IDCompositionDesktopDevice` in managed code. Avalonia never implements them: it calls the system's DirectComposition through its proxies, so these entries are never reached. The release script accepts exactly these two and fails on any other trim or AOT warning in the app.

`harness-ui-app ADDRESS --smoke` is the release smoke check. It prints `harness-ui-app: window` when the window opens. It then prints either `harness-ui-app: ui` once the UI has loaded (exit 0) or `harness-ui-app: failed: …` (exit 3), and exits. If the UI has not loaded after 90 s, it prints `harness-ui-app: timeout` and exits with 4.

A local `dotnet publish src/Harness.Ui.Desktop -c Release -r win-x64 -p:PublishProfile=Release` builds the same executable. It needs the native toolchain for the target platform.

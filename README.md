# Harness

Harness is a local project-memory and code-intelligence MCP server for Claude
Code and Codex, with a local web UI.

## Supported releases

| Platform | Release RID | Archive suffix |
| --- | --- | --- |
| Windows x64 | `win-x64` | `windows-amd64.zip` |
| Linux x64 | `linux-x64` | `linux-amd64.tar.gz` |
| Linux ARM64 | `linux-arm64` | `linux-arm64.tar.gz` |
| macOS Apple Silicon | `osx-arm64` | `darwin-arm64.tar.gz` |

Intel macOS (`osx-x64`) is no longer supported.

Extract the main archive into one directory and put that directory on your PATH.
Keep the included SQLite native library (`e_sqlite3.dll`, `libe_sqlite3.so`, or
`libe_sqlite3.dylib`) beside `harness`, `harness-graphify` and `harness-agents`.
The browser application is embedded in `harness`. These are trimmed
Native AOT .NET 11 executables: no preinstalled .NET, Node, or
Python runtime is required for the core. Git is required for sync/history.
Optional provider backends have their own prerequisites. The separate
developer-tools archive contains `harness-collect` and `harness-share`.

### Desktop app

The `harness-ui-app-VERSION-OS-ARCH` archive holds `harness-ui-app`
(`harness-ui-app.exe` on Windows): harness ui in a native window instead of a
browser tab. Install the main archive too. The app uses a harness ui already
answering on `127.0.0.1:17477`, or on the address passed as its first argument or
in `HARNESS_UI_ADDR`. If none answers on a local address, it starts
`harness ui --no-open` with the `harness` beside it or on PATH, and stops that
server when its window closes. Extract it beside `harness` or anywhere else, and
keep its native libraries beside it: `libSkiaSharp` and `libHarfBuzzSharp`,
`av_libglesv2.dll` on Windows, and `libAvaloniaNative.dylib` on macOS. harness
never installs it.

```shell
harness version
harness init
harness install --client all
harness doctor
harness ui
```

The UI listens on `127.0.0.1:17477` by default.

## Platform prerequisites

Keep the operating system updated before running the release. Native releases require
the system libraries listed below. Validate the exact archive on its target host;
see the release guide for the automated checks.

### Linux

Self-contained executables still depend on native OS libraries. A tested Linux
machine needed a system update and the OpenSSL 3 package `libssl3t64`. On
Debian/Ubuntu releases that provide this package:

```shell
sudo apt-get update
sudo apt-get install libssl3t64
```

Package names and other dependencies vary by distribution and OS version. Follow
Microsoft's [.NET on Linux guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux)
and the linked instructions for your distribution if libraries are missing.
Installing a .NET SDK/runtime is not required for these release binaries.

The desktop app (`harness-ui-app`) needs a graphical session: X11, or XWayland
under Wayland. It also needs fontconfig, libICE and libSM. On Debian/Ubuntu:

```shell
sudo apt-get install libfontconfig1 libice6 libsm6
```

### macOS Apple Silicon

The tested macOS installation worked after a system update. Releases are not
Developer ID signed or notarized, so macOS may mark downloaded executables with
quarantine and block them until you explicitly trust them.

Verify the download against `SHA256SUMS`. After attempting to run a trusted
binary, open **System Settings > Privacy & Security > Open Anyway**, then confirm
**Open**, as described in [Apple's instructions](https://support.apple.com/en-us/102445).
Repeat for `harness-graphify` and `harness-agents` if they are blocked separately.

For trusted command-line downloads, you can instead remove the quarantine
attribute from the specific extracted binaries:

```shell
xattr -d com.apple.quarantine ./harness ./harness-graphify ./harness-agents ./libe_sqlite3.dylib
```

Apply the same per-file step to `harness-collect` or `harness-share` if you use
the optional developer tools. Keep Gatekeeper enabled for the rest of the system.

The desktop app is a plain executable, not a signed `.app` bundle. Trust it, and
its native libraries, the same way:

```shell
xattr -d com.apple.quarantine ./harness-ui-app ./libSkiaSharp.dylib ./libHarfBuzzSharp.dylib ./libAvaloniaNative.dylib
```

## Documentation

- [Architecture and module documentation](doc/architecture/README.md)
- [Documentation index and usage guides](doc/README.md)
- [Build, test and benchmark instructions](doc/testing.md)
- [Release packaging and native dependencies](doc/releases.md)
- [C# formatting and development conventions](doc/csharp-formatting.md)

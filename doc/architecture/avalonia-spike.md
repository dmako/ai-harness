# Spike: harness ui in Avalonia, served as WebAssembly (TASK-0144)

**Recommendation: go**, under three conditions the parity work must carry:

1. **Accessibility is a gap.** Avalonia 12.1.6 in the browser gives screen readers nothing but a canvas, and the browser's
   Ctrl+F finds nothing. The user accepted canvas rendering when choosing Avalonia over Blazor, so this is a known
   cost, not a blocker. Parity needs its own in-app search, and harness needs to watch Avalonia's browser
   accessibility work.
2. **SkiaSharp is pinned past what Avalonia asks for.** .NET 11's WebAssembly toolchain cannot link the SkiaSharp
   and HarfBuzzSharp versions Avalonia 12.1.6 depends on. Newer releases (SkiaSharp 4.156.0, HarfBuzzSharp
   14.4.0.100) link and run, but Avalonia does not list them as supported. Each Avalonia or SkiaSharp upgrade has to
   be checked against this.
3. **Building the browser host needs the `wasm-tools` workload**, both locally and in the release pipeline.

What it gives in return:

- one C# UI shared by the browser and a desktop host;
- styles and components in separate files;
- typed API contracts;
- headless tests that reproduce most of what the Edge UI checks verify today, faster and without a browser.

The Board loads in **1.6–2.3 s cold** (4.9 MB transferred) and **about 1.4 s warm**. The JavaScript UI is about
0.6 MB.

## What was built

| Piece | Where | What it is |
| --- | --- | --- |
| Shared UI | `src/Harness.Ui` | `App`, the views (`MainView`, `BoardView`, `TaskCard`) and view models (`MainViewModel`, `BoardViewModel`). It also holds a typed API client (`HarnessApi` with source-generated `UiJsonContext`, `ServerSentEvents`) and styles split into `Styles/Tokens.axaml` (semantic colours per theme, sizes), `Blueprints.axaml` (the 16 blueprint colours), `Base.axaml`, `Board.axaml` and `BlueprintFrames.axaml`. It references no harness project. |
| Browser host | `src/Harness.Ui.Browser` | `Microsoft.NET.Sdk.WebAssembly`, `net11.0-browser`. `Program` reads the page address and the deep link and starts the shared `App` in `#out`. `wwwroot/` holds `index.html`, `main.js` (runtime start, timing marks) and `main.css`. |
| Serving | `src/Harness.Web/UiNextAssets.cs` | `/next/` serves the publish output's `wwwroot` from the folder named by `HARNESS_UI_NEXT_DIR`; when the variable is unset, `/next/` does not exist. It serves the `.br` copy when the browser accepts Brotli, with ETag/Last-Modified and `no-cache`. Paths are guarded by the host's own path checks plus a containment check. |
| Tests | `test/Harness.Ui.Tests` | 12 headless tests on Avalonia's headless platform with Skia. |
| Tests | `test/Harness.Integration/Web/UiNextTests.cs` | `/next/` content types, policy, Brotli, redirect, path guard, and its absence when unset. |

The current UI at `/` is unchanged.

To try it:

```
dotnet publish src/Harness.Ui.Browser -c Release -o artifacts/next     # needs the wasm-tools workload
HARNESS_UI_NEXT_DIR=<repo>/artifacts/next/wwwroot harness ui           # then open /next/#/p/<project id>/board
```

The bundle is served from a folder, not embedded. That keeps the release pipeline and the `harness` executable
unchanged during the spike. Embedding it is a parity task (see [the plan](#follow-up-blueprint)).

## Versions and build

| Item | Finding |
| --- | --- |
| Avalonia | **12.1.6** (Avalonia, Avalonia.Browser, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Headless, Avalonia.Skia). Its packages carry `net10.0` and `net10.0-browser1.0` assets and nothing for net11.0. They work from `net11.0` and `net11.0-browser` through framework compatibility. |
| .NET | SDK 11.0.100 (pinned by `global.json`). The browser host targets `net11.0-browser`. |
| Workload | **`wasm-tools`** (11.0.100). SkiaSharp's targets set `WasmBuildNative=true`, so every build of the browser host relinks `dotnet.native.wasm` with Emscripten. Without the workload it cannot build. |
| **Blocker found and worked around** | SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3 (what Avalonia 12.1.6 asks for) ship WebAssembly libraries built for Emscripten 3.1.56 (.NET 9/10). .NET 11 links with **Emscripten 6.0.2** and the new WebAssembly exceptions, so the link fails with `undefined symbol: saveSetjmp / testSetjmp`. **SkiaSharp 4.156.0 and HarfBuzzSharp 14.4.0.100** add `6.0.2` builds selected for `net11.0`. `Directory.Packages.props` pins them, together with their native asset packages, through central transitive pinning. Avalonia.Skia 12.1.6 runs with them in the browser and on the Windows headless platform. |
| Solution | `Harness.Ui` and `Harness.Ui.Tests` build with the solution. `Harness.Ui.Browser` is in `Harness.slnx` with `<Build Project="false" />`, so `dotnet build/test Harness.slnx` (CI, no workload) is unaffected. It is built with `dotnet publish`. |
| Dependency rules | `Directory.Build.targets`: `Harness.Ui` references no harness project; `Harness.Ui.Browser` references only `Harness.Ui`; no other rule-checked project may reference either. A forbidden reference fails the build ("Harness.Ui has forbidden project references"). |
| Static web assets | The WebAssembly SDK needs them: the browser host sets `StaticWebAssetsEnabled=true`, overriding the repository default. |
| Trimming | Release publish of the browser host trims with **no** trim or AOT warnings: compiled bindings throughout and source-generated JSON. `Harness.Ui` is `IsAotCompatible` like every `src` project. |
| Publish time | About 65 s for Release (native relink plus trimming). |
| Locked restore | `dotnet restore Harness.slnx --locked-mode` passes, and every new project has a lock file. On this machine it failed now and then with NU1004 on *unchanged* projects (Harness.Core and others), because the evaluation lacked `Microsoft.NET.ILLink.Tasks`. `dotnet build-server shutdown` plus `-nodeReuse:false` cleared it every time, so stale reused MSBuild nodes are involved. A clean HEAD worktree did not show it. |

## The API seen from C#

- **There were no typed contracts to reuse.** harness ui builds its answers as `JsonObjectMap`s in `Harness.Cli`'s `UiReadService`; `Harness.Web`'s `ProjectJsonContext` only knows primitives and `JsonNode`. The spike wrote the first typed contract (`Harness.Ui/Api/Contracts.cs`: projects, Board, Board change, live change, error; snake_case through `UiJsonContext`).
  For parity the contracts should move to a project that both the server and the UI reference, or be pinned by contract tests against the server.
- **Live updates use a streamed `fetch`.** `HttpClient` in the browser streams a response body when the request sets the option `WebAssemblyEnableStreamingResponse`; the body then arrives as fetch's `ReadableStream`. `ServerSentEvents` parses the `text/event-stream` in C#. It is not the browser's `EventSource`, so it has no built-in reconnect: `MainViewModel` reconnects after 3 s and reloads. A change naming the shown project reloads its Board after 500 ms of quiet. Measured: one `task_update` caused **3 reloads**, because one call writes several change events. Parity should add the JavaScript UI's 3 s minimum gap between reloads.
- **Writes**: the browser sends `Origin` itself, so `UiWrites`' origin check passes. Any other host (desktop, tests) must set `Origin: http://<host>` and `X-Harness-UI: 1`.
- **Content Security Policy**: `/next/` sends `default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; frame-ancestors 'none'`. `'wasm-unsafe-eval'` allows compiling WebAssembly only, not JavaScript `eval`. No inline-style allowance was needed, and the browser logged no policy violation.

## Bundle size and load time

Release publish, trimmed, `InvariantGlobalization`: **53 files, 17.60 MB raw, 4.81 MB Brotli, 6.55 MB gzip.**

| File | Raw | Brotli |
| --- | --- | --- |
| `dotnet.native.wasm` (runtime plus Skia plus HarfBuzz) | 8.08 MB | 2.18 MB |
| `Avalonia.Fonts.Inter` (embedded font) | 1.89 MB | 0.47 MB |
| `System.Private.CoreLib` | 1.87 MB | 0.56 MB |
| `Avalonia.Base` | 1.61 MB | 0.44 MB |
| `Avalonia.Controls` | 0.81 MB | 0.26 MB |
| `Avalonia.Themes.Fluent` | 0.66 MB | 0.08 MB |
| `Harness.Ui` | 0.14 MB | 0.04 MB |

For comparison, the JavaScript UI is `app.js` 425 KB plus `style.css` 149 KB plus `brand.css` 14 KB.

**Release archive growth:**

| What is shipped | Added size |
| --- | --- |
| Raw files only (the archive compresses them) | +6.9 MB |
| `.br` copies only (harness ui serves Brotli; it would have to decompress for the rare client without it) | about +4.8 MB |
| Both | +12.2 MB |

The installed executables are 107 MB today.

**Load time to an interactive Board.** "Board drawn" is the first frame after the Board's data is applied, marked
from C# (`window.harnessTimings.board`, milliseconds since navigation). Measured with headless Edge, Windows 11,
loopback, a real project with 10 cards on the Board.

| Load | Board drawn | Transferred | Notes |
| --- | --- | --- | --- |
| First load, empty cache (7 runs) | 1.6–2.3 s (median about 1.9 s) | 4.86 MB in 50 requests | the .NET runtime is ready at about 0.3–0.4 s; Avalonia's start and the first layout take the rest |
| Reload, warm cache (9 runs) | 1.2–2.2 s (median about 1.4 s); one outlier at 4.1 s | 100 KB (`304`s) | in 1 of 10 reloads the Board did not appear within 30 s; it did not reproduce in the next 6 |

A warm load is dominated by instantiating the runtime and starting Avalonia, not by the network. Fingerprinted
`_framework` files could be served `immutable` to skip the 50 revalidations. WebAssembly AOT would trade a larger
bundle for faster code. Neither was tried.

## Browser behaviour

Checked in headless Edge through the DevTools protocol: real input events, the clipboard with permission granted,
emulated colour scheme and device pixel ratio.

| Behaviour | Result | Detail |
| --- | --- | --- |
| Text selection and copy | **works, where chosen** | Card titles are `SelectableTextBlock`s: a double-click selected a word ("version"), and a drag over a title followed by Ctrl+C put "SCM connection URL aliases for project detection" on the clipboard. Plain `TextBlock`s (labels, ids, counts) cannot be selected, and there is no page-wide Ctrl+A. *Workaround:* use `SelectableTextBlock` wherever copying matters, or add copy buttons. |
| Ctrl+F (find in page) | **gap** | The page has no text in the page's DOM (`document.body.innerText` is the 16-character placeholder; `window.find()` finds nothing), so the browser's find cannot see the UI. *Workaround:* an in-app search or filter on each list view. |
| Links: left click | **works** | A task id is a `HyperlinkButton`; clicking it opened a new tab at the current UI's task URL. |
| Links: middle-click, context menu, hover URL | **gap** | Middle-click opened nothing; there is no "Open in new tab" or "Copy link" menu and no status-bar URL on hover. *Workaround:* an Avalonia context menu with "Open in new tab" and "Copy link" (`window.open`, the clipboard). |
| Deep link into a project | **works** | `/next/#/p/<id>/board` opens that project's Board. Choosing another project rewrites the hash (`history.replaceState`), so the address stays shareable. Back and Forward are not wired (replace, not push): parity should push and listen to `hashchange`. |
| Browser zoom | **works** | Emulated at 2× device pixel ratio: crisp text and controls, layout reflows. It first showed a real bug: columns squeezed until their headers were cut. The fix: the page scrolls vertically below `PageMinHeight` (520 px); above it, each column scrolls on its own. |
| Keyboard | **works** | Tab moves through the project, sort, done-age and blueprint pickers, then the cards' links and titles, with a visible focus frame in the browser. The headless test `TabMovesThroughThePickersAndCards` asserts the pickers' order. Opening a picker from the keyboard was not checked. |
| Screen reader exposure | **gap** | The accessibility tree holds only `Canvas`. Avalonia 12.1.6 in the browser does not expose its automation peers (the `AutomationProperties.Name`s set on pickers, columns and cards reach nothing). No setting for it was found. The spike also removes the "Loading harness…" placeholder once the Board is drawn, because a screen reader would otherwise still read it under the canvas. |
| Light and dark theme | **works** | Avalonia follows `prefers-color-scheme`; the semantic tokens switch through `ThemeDictionaries`. |
| 390 px width | **works** | The header and pickers wrap, and the Board scrolls sideways inside itself, never the page. Columns keep 232 px (the JavaScript UI uses 85vw on narrow screens). |
| Live updates | **works** | A change to the project in the database redrew the Board without a page reload. The status shows "● live" once the stream is open. |

## Headless tests

`test/Harness.Ui.Tests` runs the shared UI on Avalonia's headless platform with Skia drawing, against `FixtureApi`, an
`HttpMessageHandler` with fixed answers, recorded POSTs and an endless `/api/events` stream the test writes into.
12 tests, about 2.5 s in total:

- the Board shows the server's columns and cards: priority and blueprint colour classes, a card without a blueprint, the task link;
- choosing a sort POSTs `{"sort":"id"}` with `X-Harness-UI: 1` and reloads the Board;
- a live change to another project does nothing, and one to the shown project redraws it;
- the UI checks at 1280×800 (light and dark), 390×844 (light and dark), 800×300 and 800×160, plus a captured Skia frame;
- the WCAG AA contrast of every text colour the Board draws, light and dark;
- the keyboard Tab order.

**Which UiBrowserTests checks can be reproduced headlessly:**

| Check | Headless | How |
| --- | --- | --- |
| Overflow (the page never scrolls sideways) | yes | `PageScroller.Extent.Width <= window width`. |
| Clipped text | yes | A `TextBlock` whose laid-out lines (`TextLayout.Height`) exceed its bounds, or which a clipping ancestor cuts. Scrollers only count along the directions they cannot scroll; beyond a scroller, only its visible part is checked further up. Verified against the zoom bug: with the fix undone, the 800×160 case fails ("column backlog is 47 px"); with it, the case passes. |
| 24 px controls | yes | `Bounds` of `ComboBox`, `HyperlinkButton` and `Button`. Scroll bar parts are excluded: Fluent's arrows are 16 px and appear on hover, and the browser checks leave native scroll bars out too. |
| Contrast (WCAG AA) | yes, by token | Contrast of the brush pairs the views use, computed from the theme resources rather than from pixels. It found that **dark `Critical` (#e0605a) on `Surface2` is 4.38:1**, the same value as `brand.css`, so the design system must not draw critical text on `Surface2`. Pixel-level checks are possible on `CaptureRenderedFrame` but were not built. |
| Both themes, both widths | yes | `RequestedThemeVariant` and the window size. |
| No JS errors, CSP, fetch streaming, WebGL, clipboard, device pixel ratio, Ctrl+F, screen reader | **no** | These need a real browser. Parity should keep a thin browser smoke test: the spike's DevTools driver is a sketch of one. |

**Two traps in the test setup:**

- **Avalonia.Headless.XUnit 12.1.6 does not work with this repository's xunit.v3 4.x.** It is built against xunit.v3 3.2, and discovery fails with `MissingMethodException` (`TestIntrospectionHelper.GetTestCaseDetails`). The tests use `Avalonia.Headless`'s `HeadlessUnitTestSession` directly, through `Ui.RunAsync`.
- **`HeadlessUnitTestSession.Dispatch` with an async lambda binds to the `Func<TResult>` overload.** The test then ends at its first `await`: it passes without running its assertions, or hangs once awaited. Only `Func<Task<T>>` keeps the dispatcher running until the test ends. `Ui.RunAsync` wraps every test so.

## Follow-up blueprint

Proposed as a blueprint, "Avalonia harness ui: parity", for the user to approve. In order:

1. **Foundations.**
   - Shared API contracts project (or contract tests) for every endpoint the UI reads or writes.
   - Navigation shell: sidebar, project and workspace routes as `#/p/<id>/<view>`, Back and Forward through `hashchange`.
   - Design system in Avalonia resources: all brand tokens and component styles from `brand.css`/`style.css`, icons.
   - Toasts and dialogs.
   - The live-update coalescing (500 ms quiet, 3 s gap).
   - Link affordances: "Open in new tab" and "Copy link" menus.
2. **Views, in the current UI's order of use:**
   1. Project Overview
   2. Work (tasks, blueprints, inbox proposals, with Edit and Delete)
   3. Board (done)
   4. Knowledge (memories, proposals, sharing)
   5. Agents (sessions, tools and providers, SCM)
   6. Architecture (structure, layers, complexity, coupling, packages)
   7. Health
   8. Workspace views (overview, architecture, knowledge, issues, members)
   9. Machine, Search, Docs
3. **Embedding and release.**
   - Embed the published bundle in `Harness.Web` as resources (or ship it beside the executables), serve it at `/`, and retire `app.js`.
   - Install the `wasm-tools` workload in `tests.yml` and `release.yml` and publish the browser host once (its output is the same on every platform).
   - Serve fingerprinted files `immutable`.
   - Decide `.br`-only versus raw shipping (+4.8 MB versus +6.9 MB).
4. **Tests.**
   - Headless view tests per view with the layout and contrast checks above, as a shared helper.
   - A small browser smoke test (load, no console errors, live update) replacing most of `UiBrowserTests`.
5. **Accessibility track.**
   - An in-app search for Ctrl+F's role.
   - Follow Avalonia's browser accessibility work and record what is still missing before `/` switches over.

The desktop host is TASK-0145 (next in BP-009). It reuses `Harness.Ui` unchanged and must set the `Origin` header for
writes.

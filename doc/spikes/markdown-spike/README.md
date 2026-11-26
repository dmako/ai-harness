# Spike: a streaming Markdown renderer for the Avalonia UI? (TASK-0205)

**Recommendation: try MarkView first, but adopt neither package yet.** Harness kept its own `MarkdownBlock` while the
spike ran; it changed no production renderer.

**What came of it:** harness adopted MarkView with its Mermaid extension for documentation and knowledge (TASK-0206,
commit `8d18442`); syntax highlighting is deferred. See [Integration follow-up](#integration-follow-up-task-0206).

The question: which Avalonia Markdown viewer should replace `MarkdownBlock`, with GitHub-flavoured Markdown, streaming
updates, Unicode and emoji, Mermaid, and a working WebAssembly (browser) publish on .NET 11? The candidates were
LiveMarkdown.Avalonia 2.7.0 and MarkView.Avalonia 12.4.1. How it was measured, and the commands to rerun it, are in
[measurements.md](measurements.md).

## Recommendation

Investigate MarkView first for a replacement, but do not adopt either package yet. MarkView's core browser publish succeeds in this comparison, its GFM and streaming APIs fit the use cases, and it has a smaller measured cost. Its behavior still needs visual, accessibility, security and real-browser testing inside harness. LiveMarkdown is a capable streaming viewer, but its core package's TextMateSharp dependency currently blocks the .NET 11 browser publish. A compatible `Onigwrap` WebAssembly build or a dependency change is needed before it can be evaluated fairly in the shipped host. These findings are version-specific, not claims that either library is generally incompatible with Avalonia or WebAssembly.

| Concern | LiveMarkdown 2.7.0 | MarkView 12.4.1 |
| --- | --- | --- |
| License and dependencies | Apache-2.0; core brings Markdig 1.1.2, TextMateSharp and grammars 2.0.0. | MIT; core brings Markdig 1.4.1. Syntax highlighting is a separate package. Check transitive notices before shipping either. |
| Streaming | `ObservableStringBuilder` append/clear drives incremental updates. Its builder is not thread-safe; marshal updates to the UI thread. Incomplete Markdown and streaming performance require application-level testing. | `BeginStreaming()` opens one append session per viewer, and its documented incremental update preserves unaffected controls. Starting the session is on the UI thread; see its [streaming guide](https://kryptos-fr.github.io/MarkView.Avalonia/guides/streaming/). |
| GFM | Markdig-based viewer with tables and extensible pipelines. Full GFM parity, task-list behavior and HTML policy have not been verified against harness fixtures. | Documents GFM tables, task lists, alerts, footnotes and an HTML subset; actual coverage and HTML/link/image policy need harness tests. |
| Unicode and emoji | Unicode text passes through the renderer. The sample includes a ZWJ emoji, but glyph appearance, fallback and color were not visually qualified. | Same qualification; Unicode input is supported but glyphs and color depend on available fonts and rendering platform. Harness browser currently embeds Inter, not an emoji font. |
| Mermaid | Optional `LiveMarkdown.Avalonia.Mermaid`, registering a pipeline extension and node, backed by Mermaider. Not included in measured builds. | Optional `MarkView.Avalonia.Mermaid` / `UseMermaid()`, backed by Mermaider. Not included in measured builds. |
| Editing | Viewer, not a general inline editor. Use a separate Markdown editor and preview; append/clear streaming is not arbitrary in-place editing. | Viewer, not a general inline editor. Interactive task lists are narrower than arbitrary Markdown editing; use a separate editor and coordinate it with an active stream. |
| Integration | Add compiled theme and resource includes, map links/navigation, reconcile selection and find, marshal chunks, and retain existing safe-link policy. Core pulls syntax highlighting whether used or not. | Add compiled theme, map link handling, reconcile selection/find and streaming lifecycle, and configure optional extensions. Its richer features and HTML/image loading need explicit security and accessibility review. |
| Maintenance | One named upstream maintainer; [repository and release history](https://github.com/DearVa/LiveMarkdown.Avalonia/releases) show active development. | One named upstream maintainer; [repository and release history](https://github.com/Kryptos-FR/MarkView.Avalonia/releases) show active development. Both are smaller third-party projects; release activity is not a support guarantee. |

Feature descriptions are based on the pinned packages' bundled README and manifests, and the upstream [LiveMarkdown README](https://github.com/DearVa/LiveMarkdown.Avalonia/blob/05203e511542059a9e4eb1b1421093d0db2fb44e/README.md) and [MarkView README](https://github.com/Kryptos-FR/MarkView.Avalonia/blob/790cbc20145c6ceb52b25b3d98acd1f163e6b152/README.md). Upstream descriptions are not independent proof of visual correctness or complete GFM conformance.

## Build results and limits

| Build | Baseline | LiveMarkdown | MarkView |
| --- | ---: | ---: | ---: |
| Windows x64 Native AOT published files, excluding PDB | 40,930,569 B | 54,510,857 B (+13,580,288 B) | 46,263,561 B (+5,332,992 B) |
| Windows x64 executable | 19,580,928 B | 32,620,544 B (+13,039,616 B) | 24,913,920 B (+5,332,992 B) |
| Browser `wwwroot` Brotli artifacts | 4,583,003 B | publish failed | 5,002,033 B (+419,030 B) |

All three native publishes succeeded and each process stayed alive for three seconds when launched with its window hidden. This only checks startup, not that every control rendered correctly. All report the existing Avalonia Win32 DirectComposition non-blittable callback warnings; those paths remain unqualified. Browser baseline and MarkView published with release trimming and wasm-tools. LiveMarkdown's browser publish failed because `Onigwrap` 1.0.9 requests `wasm/6.0.3/st/libonigwrap.a`, which its package does not contain (it only contains archives through 3.1.56). The installed .NET 11 workload uses Emscripten 6.0.2. Inspection of the newer Onigwrap 1.0.11 package also found no 6.x archive. No LiveMarkdown browser size is reported; do not treat a failed or partial publish as a measurement. Merely substituting an older archive would not establish ABI compatibility.

The browser builds were not run in an actual browser, and native visual rendering, color emoji, streaming updates, selection, navigation, screen readers, editing and Mermaid were not tested. Linux and macOS native builds, cross-target Native AOT, optional packages, and full harness release-bundle deltas remain unverified. Re-run the spike with a fresh output path and compare a real product integration before deciding to ship.

## Integration follow-up (TASK-0206)

Harness uses MarkView and its Mermaid extension in documentation and knowledge. The user deferred syntax highlighting: MarkView's TextMate extension depends on Onigwrap 1.0.11, whose package does not include the WebAssembly archive required by the current .NET 11 workload. Code fences render without syntax colors. The browser Release publish and Windows x64 Native AOT publish succeed with the viewer and Mermaid; the latter reports the pre-existing Avalonia DirectComposition warnings described above. A headless test renders a Mermaid diagram and checks light and dark theme resources. Browser emoji glyphs still depend on available fonts; color emoji and other native platforms have not been visually qualified.

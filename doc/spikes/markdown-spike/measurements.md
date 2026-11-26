# Markdown renderers spike: how it was measured (TASK-0205)

**Recommendation: try MarkView first; adopt neither package yet.** The findings and the reasoning are in
[README.md](README.md). This page describes the spike's program, its commands and the machine it ran on. The runs'
numbers are in the README's build results; no other raw output was kept, so there is no `results/` folder.

The spike's code was removed from the repository in TASK-0213. It was under `doc/spikes/markdown-comparison/`. To
rerun it, check out commit `cb74345`, the last one that has it.

## Versions and machine

- Run on 19 November 2026 on the Windows x64 development machine, with .NET SDK 11.0.100 and the wasm-tools workload
  (Emscripten 6.0.2).
- Avalonia 12.1.6, with the repository's SkiaSharp 4.156.0 / HarfBuzzSharp 14.4.0.100 pins.
- Candidates: LiveMarkdown.Avalonia 2.7.0 and MarkView.Avalonia 12.4.1, against harness's own `MarkdownBlock`.
- Not run: Linux and macOS native builds, cross-target Native AOT, a real browser.

## The program

Two hosts, a Windows desktop one (`Desktop/`) and a WebAssembly one (`Browser/`), shared `Shared.cs` and one sample:
headings, an emoji, emphasis, a link, a table, a task list and a code fence. The `Candidate` property chose the
renderer:
- `Candidate=baseline` instantiated the existing `Harness.Ui.Controls.MarkdownBlock`.
- `live` and `markview` instantiated the respective package.

Each candidate's styles were included in compiled XAML (`Styles/<candidate>.axaml`). Both hosts referenced the same
Harness.Ui project and the same native-library pins. Their own `Common.props` and `Directory.*` files kept them out of
the repository's build rules and central package versions.

The sample exercised construction, not streaming or exhaustive syntax conformance. The extra reference to Harness.Ui is
trimmed on publish; the results describe this **small host**, not an exact whole-product delta. No Mermaid, math or
optional syntax-highlighting packages were added.

## Commands

Use a fresh output directory for each run, with the repository SDK and wasm-tools installed. From the repository root
in PowerShell, at the commit above:

```powershell
foreach ($candidate in @('baseline', 'live', 'markview')) {
  dotnet restore doc/spikes/markdown-comparison/Desktop/Desktop.csproj -p:Candidate=$candidate -p:RuntimeIdentifier=win-x64 -p:PublishAot=true
  dotnet publish doc/spikes/markdown-comparison/Desktop/Desktop.csproj -c Release -r win-x64 -p:Candidate=$candidate -p:PublishAot=true --no-restore -o "artifacts/markdown-comparison/aot-$candidate" -m:1 -nodeReuse:false
  dotnet restore doc/spikes/markdown-comparison/Browser/Browser.csproj -p:Candidate=$candidate
  dotnet publish doc/spikes/markdown-comparison/Browser/Browser.csproj -c Release -p:Candidate=$candidate --no-restore -o "artifacts/markdown-comparison/browser-$candidate" -m:1 -nodeReuse:false
}
```

The loop intentionally continues to the next candidate if one publish fails; inspect each exit code and output.

## How the sizes were counted

- **Native size** is the sum of the published files, recursively, excluding `.pdb`. The executable's size is reported
  separately.
- **Browser size** sums all `.br` files under the published `wwwroot`. It compares compressed artifact sets; it is not a
  measurement of a browser's first-load requests. It excludes uncompressed-only files and optional extensions.
  Published directories may hold several encodings; do not add them together as a download.
- **Startup**: each native publish was launched with its window hidden and had to stay alive for three seconds. This
  checks startup only, not that every control rendered correctly.

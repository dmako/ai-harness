# Code metrics

Cyclomatic complexity findings (milestone `Metrics`) come from two sources.
Harness counts complexity lexically from the tokens its clone extractor already
reads, on every sync. For .NET projects, a background worker can replace those
counts with exact values from `Microsoft.CodeAnalysis.Metrics`. This page
records how that worker runs the tool (spike TASK-0040, 2026-10-25).

## Running Microsoft.CodeAnalysis.Metrics

Harness runs the package's `Metrics.exe` directly from a cache it owns. It
passes each project explicitly with `/project:` and writes `/out:` under harness
state. Nothing is added to the analysed repository, and its working tree stays
unchanged. Only restore output, which is normally ignored, is created, and only
when a project was never restored.

- The cache holds the package's `Metrics/` folder plus the `BuildHost-net472`
  and `BuildHost-netcore` folders from `Microsoft.CodeAnalysis.Workspaces.MSBuild`.
  Releases 4.14.0 to 5.6.0 omit the build host, so SDK-style projects fail with
  "The build host could not be found". Version 5.6.0 bundles Workspaces.MSBuild
  4.12.0-3.24572.7, and the build host from package 4.12.0 works with it. Pin
  both versions.
- `/solution:` rejects `.slnx` files. Harness passes the projects it already
  knows from its build reader instead. They go in dependency order, lowest
  first: a project that an earlier project already loaded as a reference fails
  with "already part of the workspace". Test projects can be left out.
- The tool needs restored projects (`obj/project.assets.json`). It does not
  restore them itself.

The alternative is `dotnet msbuild /t:Metrics` with the package targets injected
through `CustomAfterMicrosoftCommonTargets` and `MetricsOutputFile` redirected.
It works and leaves the tree unchanged. However, it runs the same executable
once per project, and each run loads all of that project's references again.

| On this repository | Direct run (chosen) | msbuild `/t:Metrics /m` |
| --- | --- | --- |
| Projects | 9 product projects | 13, including tests |
| Wall time | 18.2 s | 22.3 s |
| Peak memory | ~0.4 GB tool + ~0.1 GB build host | ~1.8 GB over 13 processes |
| One project | 4.5 s (Core) to 6.5 s (Cli) | n/a |

A method's complexity depends only on its own project's source. After a sync,
the worker reruns only the projects that own changed files, in one process. The
fixed cost is about 4 s per run.

## Platforms

`Metrics.exe` targets .NET Framework 4.7.2 (PE32, `supportedRuntime v4.0`), and
the package's targets call the `.exe` directly. It runs on Windows only. On
Linux and macOS, harness keeps its lexical counts. If exact values are needed
there later, a small JIT-compiled .NET worker could call
`CodeAnalysisMetricData.ComputeAsync` (`Microsoft.CodeAnalysis.AnalyzerUtilities`)
through MSBuildWorkspace. It cannot be part of the Native AOT executables.

## Report mapping

Each `Method`, `Property` and `Field` element has `File` and `Line`. `Line` is
the line of the member's name (after attributes; for a multi-line signature,
the line where the name appears), the same as the line harness records for an
extracted function. Findings join on path, line and name, which also tells
overloads apart. Property accessors are nested under `Accessors`, and the
property reports the highest accessor value. Lambdas, anonymous methods and
local functions have no entries of their own: their branches count toward the
containing member.

## Counting rules

A fixture compared with the tool's output gives these rules:

- **Adds one:** `if`; each `case` label; `for`, `foreach`, `while` (a
  `do … while` counts once); `?:`; `?.`; `??`; `&&`; `||`.
- **Adds nothing:** switch-expression arms, `catch`, exception filters (`when`),
  pattern `and`/`or`, `??=`.

Branches inside interpolated-string holes count too: `$"{(a ? 1 : 0)}"` adds
one.

With these rules, a prototype lexical counter matched the tool exactly on 25 of
30 sampled methods and gave the same band on 28. The mean difference was 0.2
and the largest was 2. The two band mismatches were 20 against 21 and 20
against 22. Counting the constructs that the tool ignores matched only 11 of
30. The lexical reader therefore follows the tool, so the level does not change
when exact values replace the lexical counts.

The implemented reader (TASK-0037) matched the tool on 97.5% of the 1661 methods
on this repository that both report, and gave the same band on 99.6%. It found
all 179 methods above 10. The six band mismatches all count too low. Each comes
from an interpolation hole that itself contains quotes
(`$"{report["harness"]?["version"]}"`): the shared lexer ends the string token
at the inner quote. Generic, tuple-returning, expression-bodied and constructor
members are counted; properties are not, because none on this repository
exceeds 10.

On this repository's 2206 methods, the default thresholds give 140 notes
(11–20), 45 warnings (21–50) and 6 problems (above 50).

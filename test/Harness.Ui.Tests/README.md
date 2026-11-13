# Harness.Ui.Tests

Headless tests of the Avalonia harness ui (`src/Harness.Ui`). They run on Avalonia's headless platform with Skia
drawing: no window and no browser. Each test opens a view against `FixtureApi`, harness ui's API answered from files.

## Writing a view test

- Wrap every test in `Ui.RunAsync(async () => { … })`. It runs the body on the headless UI thread.
- Open the page with `Ui.OpenMainAsync(width, height)`, or a single control with `Ui.Show(control, width, height)`.
- Wait with `Ui.UntilAsync(() => condition)`, never with a fixed delay. It runs the UI thread's jobs and render ticks
  until the condition holds, for at most 5 s.
- Every view gets one test that runs `UiChecks.AssertAsync((width, height) => …open the view…)`. It checks each size
  in `UiChecks.Sizes` (1280x800, 390x844 and a short 800x300) in light and dark. The checks are:
  - **overflow**: nothing reaches past the page's side except inside a scroller that scrolls that way;
  - **clipped text**: no text is cut, either by too little height or by a clipping ancestor;
  - **24 px**: controls are at least 24 px in both directions;
  - **contrast**: WCAG AA against the background actually drawn behind the text.

  It reports every problem at once, with the size and theme each was found at. `UiChecksTests` shows it failing on
  seeded problems.

## Fixtures

`Fixtures/` holds one JSON answer per endpoint, at the route's path with its parameters kept as written:
`Fixtures/api/projects/{id}/board.json` answers `GET /api/projects/<any id>/board`.

`FixtureApi` serves them and lets a test change things:

| What | How |
| --- | --- |
| Replace an answer | `Answers[path or template]` |
| Slow an endpoint | `Delays[...]` |
| Fail an endpoint | `Failures[...]` |
| Send live events | `Change(json)` / `Agent(json)` on `/api/events` |
| See writes | `Posts`, with body, `Origin` and `X-Harness-UI` |

`Fixtures.Board(...)` returns variations of the Board file.

The fixtures must keep the server's shapes. `CommittedUiFixturesMatchTheServersShapes` in `Harness.Integration`
compares each file with the answer of a real harness ui on a seeded `SyncFixture`. Every property path in a fixture
must exist in the server's answer.

To capture fresh answers, run that test class with `HARNESS_CAPTURE_UI_FIXTURES` set to a folder. It writes the
server's answers there as `api/...{id}....json` files, with the fixture project's id replaced by `{id}`. Review them
and copy what a test needs into `Fixtures/`.

## Two traps

- **Avalonia.Headless.XUnit does not work here.** Version 12.1.6 is built against xunit.v3 3.2. Under this
  repository's xunit.v3 4.x, test discovery fails with `MissingMethodException`
  (`TestIntrospectionHelper.GetTestCaseDetails`). So tests use plain `[Fact]`/`[Theory]`, and `Ui` starts one
  `HeadlessUnitTestSession` itself.
- **Dispatch async tests only as `Func<Task<T>>`.** `HeadlessUnitTestSession.Dispatch` with an async lambda binds to
  the `Func<TResult>` overload, with `TResult` = `Task`. The session then returns at the lambda's first `await`: the
  test passes without running its assertions, or hangs once awaited. Only the `Func<Task<T>>` overload keeps the
  dispatcher running until the task ends. `Ui.RunAsync` wraps every test that way, so never call `Dispatch` directly.

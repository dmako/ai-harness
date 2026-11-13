namespace Harness.Ui.Tests;

/// <summary>Answers as harness ui gives them (shapes from UiReadService.Board and /api/projects).</summary>
internal static class Fixtures
{
    public const string ProjectId = "prj_fixture";

    public const string Projects = $$"""
        {"projects":[
          {"id":"{{ProjectId}}","title":"Fixture project","root":"C:\\repos\\fixture"},
          {"id":"prj_other","root":"/home/dev/other"}
        ]}
        """;

    /// <summary>
    /// A Board with a card in ready, in progress and done; <paramref name="title"/> names TASK-0002, and
    /// <paramref name="moreDone"/> adds that many done tasks (TASK-1000…) so the done column scrolls.
    /// </summary>
    public static string Board(string sort = "priority", string title = "Port the Board to Avalonia", int moreDone = 0) => $$"""
        {
          "settings":{"sort":"{{sort}}","hide_done_days":7,"blueprint":""},
          "defaults":{"sort":"priority","hide_done_days":7},
          "sorts":["priority","id","name","created","changed"],
          "hide_done_days_options":[1,3,7,14],
          "columns":[
            {"status":"backlog","tasks":[]},
            {"status":"ready","tasks":[
              {"id":"TASK-0001","title":"Measure the WebAssembly bundle","status":"ready","priority":"high","type":"spike","blueprint":"BP-009"}
            ]},
            {"status":"claimed","tasks":[]},
            {"status":"in_progress","tasks":[
              {"id":"TASK-0002","title":"{{title}}","status":"in_progress","priority":"critical","type":"feature","blueprint":"BP-009"},
              {"id":"TASK-0003","title":"A task without a blueprint whose title is long enough to wrap over several lines of its card","status":"in_progress","priority":"low","type":"chore"}
            ]},
            {"status":"review","tasks":[]},
            {"status":"done","tasks":[
              {"id":"TASK-0004","title":"Bump version to 0.13.0","status":"done","priority":"medium","type":"chore","blueprint":"BP-008"}{{Done(moreDone)}}
            ]},
            {"status":"blocked","tasks":[]}
          ],
          "blueprints":[
            {"id":"BP-009","title":"V13 Features and bugfixes","color":"yellow"},
            {"id":"BP-008","title":"V12 Features and Bugfixes","color":"lime"}
          ]
        }
        """;

    private static string Done(int count) =>
        string.Concat(
            Enumerable
                .Range(1000, count)
                .Select(n =>
                    $$""",{"id":"TASK-{{n}}","title":"Done task {{n}} with a title that wraps onto a second line","status":"done","priority":"low","type":"chore","blueprint":"BP-008"}"""
                )
        );
}

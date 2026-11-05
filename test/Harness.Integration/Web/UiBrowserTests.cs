using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiBrowserTests(SharedBrowser browser)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FrontendRequiresMemoryConflictChoiceAndTaskConfirmationInARealBrowser()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var memories = new MemoryStore(database);
        memories.Put(new() { Type = "decision", Content = "Template payload enums are stored as integers" }, Token);
        var proposal = memories.Propose(new("decision", "Template payload enums are stored as names"), Token).Proposal!;
        var tasks = new TaskStore(database);
        var taskProposal = tasks.Propose(
            new TaskDefinition
            {
                Title = "Browser proposed task",
                Goal = "Author reviews work in the browser",
                Acceptance = ["Author confirms"],
            },
            "Test browser review",
            "agent:test",
            Token
        );
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/knowledge/inbox" }
                );
                await page.WaitAsync("[...document.querySelectorAll('button')].some(b => b.textContent === 'Approve')");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Approve').click()"
                );
                await page.WaitAsync(
                    "document.body.textContent.includes('Replace one of them with it, or keep both:')"
                );
                memories
                    .GetProposal(proposal.Id, Token)
                    .Proposal.Status.Should()
                    .Be("pending", "the conflict choice must precede approval");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Keep both').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    memories.GetProposal(proposal.Id, Token).Proposal.Status is "approved"
                );
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/work/inbox'");
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'Create the task…')"
                );
                await page.EvaluateAsync(
                    """
                    window.originalReviewFetch = window.fetch;
                    window.holdTaskRefresh = true;
                    window.holdReviewRead = true;
                    window.fetch = async (...args) => {
                      const response = await window.originalReviewFetch(...args);
                      const url = String(args[0]);
                      if (window.holdTaskRefresh && url.includes('/task-proposals?')) {
                        window.holdTaskRefresh = false;
                        window.taskRefreshWaiting = true;
                        await new Promise(resolve => { window.releaseTaskRefresh = resolve; });
                      }
                      if (window.holdReviewRead && url.includes('/blueprints')) {
                        window.holdReviewRead = false;
                        window.reviewReadWaiting = true;
                        await new Promise(resolve => { window.releaseReviewRead = resolve; });
                      }
                      return response;
                    };
                    """
                );
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Concurrent refresh proposal",
                        Goal = "A pending refresh must preserve the review being opened",
                        Acceptance = ["Form stays connected"],
                    },
                    "Review race",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("window.taskRefreshWaiting === true");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task…').click()"
                );
                await page.WaitAsync("window.reviewReadWaiting === true");
                await page.EvaluateAsync("window.releaseTaskRefresh(); window.releaseReviewRead();");
                await page.WaitAsync("!!document.querySelector('input[aria-label=Title]')");
                tasks
                    .GetProposal(taskProposal.Id, Token)
                    .Status.Should()
                    .Be("pending", "opening the review form must not create the task");
                await page.EvaluateAsync(
                    "document.querySelector('input[aria-label=Title]').value = 'Browser reviewed task'"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.GetProposal(taskProposal.Id, Token).Status is "approved"
                );
                tasks
                    .List(cancellationToken: Token)
                    .Should()
                    .ContainSingle()
                    .Which.Definition.Title.Should()
                    .Be("Browser reviewed task");
                await page.WaitAsync("!!document.querySelector('.live.on')");
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Live update proposal",
                        Goal = "Show independent writes without reload",
                        Acceptance = ["Visible live"],
                    },
                    "SSE check",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("document.body.textContent.includes('Live update proposal')");
                await page.CommandAsync("Network.enable", new { });
                await page.CommandAsync("Network.setBlockedURLs", new { urls = new[] { "*/api/events" } });
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(
                    "!!document.querySelector('.live.off') && !document.getElementById('refresh').hidden"
                );
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Fallback polling proposal",
                        Goal = "Show independent writes while SSE is unavailable",
                        Acceptance = ["Visible through polling"],
                    },
                    "Polling check",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("document.body.textContent.includes('Fallback polling proposal')");
                await page.CommandAsync("Network.setBlockedURLs", new { urls = Array.Empty<string>() });
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync("!!document.querySelector('.live.on')");
            }
        );
    }

    [Fact]
    public async Task ReadyTaskIsEditedFromItsHeaderAndClaimedOneHasEditDisabled()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var origin = new TaskOrigin("test", "test", "fixture");
        tasks.Create(
            new TaskDefinition
            {
                Id = "UI-0001",
                Title = "Ready task",
                Acceptance = ["Old"],
            },
            "",
            origin,
            Token
        );
        tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "Claimed task" }, "", origin, Token);
        tasks.Claim("UI-0002", new("claude"), cancellationToken: Token);
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/UI-0001" }
                );
                // Edit… sits in the task's header, after its status
                await page.WaitAsync("!!document.querySelector('#task-UI-0001 .trow .pill + button.tedit')");
                (await ValueAsync(page, "document.querySelector('#task-UI-0002 .trow .tedit').disabled"))
                    .Should()
                    .Be("True");
                await page.EvaluateAsync("document.querySelector('#task-UI-0001 .tedit').click()");
                await page.WaitAsync("!!document.querySelector('dialog[open] input[aria-label=Title]')");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('a')].some(a => a.textContent.startsWith('Blueprints'))"
                            + " && !!document.querySelector('select[aria-label=Blueprint]')"
                            + " && !/milestone/i.test(document.body.textContent)"
                    )
                )
                    .Should()
                    .Be("True");
                (await ValueAsync(page, "document.querySelector('dialog[open] input[aria-label=Id]').disabled"))
                    .Should()
                    .Be("True");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('dialog[open] .lrow input')].map(i => i.value).join()"
                    )
                )
                    .Should()
                    .Be("Old");
                await page.EvaluateAsync(
                    "document.querySelector('dialog[open] input[aria-label=Title]').value = 'Edited in the browser'"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Save the task').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.Get("UI-0001", Token).Definition.Title is "Edited in the browser"
                );
                tasks.Get("UI-0001", Token).Definition.Acceptance.Should().Equal("Old");
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/work/UI-0002'");
                await page.WaitAsync("!!document.querySelector('#task-UI-0002 .tdetail')");
                (await ValueAsync(page, "document.querySelector('#task-UI-0002 .tedit').disabled")).Should().Be("True");
                (await ValueAsync(page, "!!document.querySelector('dialog[open]')")).Should().Be("False");
            }
        );
    }

    [Fact]
    public async Task TaskRowsShowTheirTypeIconBetweenIdAndTitle()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            var origin = new TaskOrigin("test", "test", "fixture");
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0001",
                    Title = "A bug",
                    Type = "bug",
                },
                "",
                origin,
                Token
            );
            tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "No type given" }, "", origin, Token);
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0010",
                    Title = "Some docs with a much longer title that has to wrap on a phone",
                    Type = "docs",
                },
                "",
                origin,
                Token
            );
        }

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/tasks" }
                );
                await page.WaitAsync("document.querySelectorAll('.trow .ttype').length === 3");
                // between the id and the title, named by its type, drawn as the Type picker draws it
                (
                    await ValueAsync(
                        page,
                        """
                        [...document.querySelectorAll('.trow')].map(r => {
                          const t = r.querySelector('.tid + .ttype + .ttitle') && r.querySelector('.ttype');
                          if (!t) return 'misplaced';
                          const d = [...t.querySelectorAll('path')].map(p => p.getAttribute('d')).join();
                          const same = d === ICONS[t.getAttribute('aria-label')].join();
                          return `${t.getAttribute('aria-label')}/${t.title}/${t.getAttribute('role')}/${same}`;
                        }).join(' ')
                        """
                    )
                )
                    .Should()
                    .Be("bug/bug/img/true feature/feature/img/true docs/docs/img/true");
                // an unknown type gets a neutral mark, still named, and no error
                (
                    await ValueAsync(
                        page,
                        "(() => { const m = typeMark('research'); return m.getAttribute('aria-label') + ' ' + m.querySelectorAll('path').length; })()"
                    )
                )
                    .Should()
                    .Be("research 1");
                const string Aligned = """
                    ['.ttype', '.ttitle'].map(s => new Set([...document.querySelectorAll('.trow ' + s)]
                      .map(e => Math.round(e.getBoundingClientRect().left))).size).join()
                    """;
                (await ValueAsync(page, Aligned)).Should().Be("1,1", "icons and titles line up across rows");
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 390,
                        height = 900,
                        deviceScaleFactor = 1,
                        mobile = true,
                    }
                );
                await page.WaitAsync("document.querySelector('.trow').getBoundingClientRect().width < 400");
                (await ValueAsync(page, Aligned)).Should().Be("1,1");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('.trow')].every(r => r.scrollWidth <= r.clientWidth"
                            + " && r.querySelector('.ttype').getBoundingClientRect().width > 0)"
                            + " && document.documentElement.scrollWidth <= innerWidth"
                    )
                )
                    .Should()
                    .Be("True", "the row fits a phone with its type icon in view");

                // each type in the Type picker's colour, at least 3:1 against the row, its hover and the open row
                const string Colours = """
                    (() => {
                      const cv = document.createElement('canvas'); cv.width = cv.height = 1;
                      const g = cv.getContext('2d', { willReadFrequently: true });
                      const px = (...colors) => { g.clearRect(0, 0, 1, 1); for (const c of colors) { g.fillStyle = c; g.fillRect(0, 0, 1, 1); } return [...g.getImageData(0, 0, 1, 1).data].slice(0, 3); };
                      const lum = (rgb) => { const [r, gr, b] = rgb.map((v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * gr + 0.0722 * b; };
                      const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
                      const probe = (v) => { const d = document.createElement('div'); d.style.color = v; document.body.append(d); const c = getComputedStyle(d).color; d.remove(); return c; };
                      const surface = probe('var(--surface)');
                      const bgs = { row: px(surface), hover: px(surface, probe('var(--surface-2)')), open: px(surface, probe('var(--selected)')) };
                      const picker = segmented('Type', TASK_TYPES.map((t) => [t, t]), 'feature', 'types', (k) => k).el;
                      document.body.append(picker);
                      const colourOf = (el) => getComputedStyle(el.querySelector('.icon')).color;
                      const faults = TASK_TYPES.map((t) => {
                        const m = typeMark(t); document.body.append(m);
                        const c = colourOf(m), pc = colourOf(picker.querySelector('.opt-' + t)); m.remove();
                        const low = Object.entries(bgs).map(([k, b]) => [k, ratio(px(surface, c), b)]).filter(([, r]) => r < 3);
                        return c !== pc ? `${t} ${c} is not the picker's ${pc}` : low.length ? `${t} ${c}: ${low.map(([k, r]) => k + ' ' + r.toFixed(2)).join(' ')}` : '';
                      });
                      // the rows themselves: a bug as the picker's bug, no type as the feature, unknown neutral
                      const row = (id) => colourOf(document.querySelector(`#task-${id} .ttype`));
                      if (row('UI-0001') !== colourOf(picker.querySelector('.opt-bug'))) faults.push('bug row ' + row('UI-0001'));
                      if (row('UI-0002') !== colourOf(picker.querySelector('.opt-feature'))) faults.push('untyped row ' + row('UI-0002'));
                      const other = typeMark('research'); document.body.append(other);
                      if (colourOf(other) !== probe('var(--ink-2)')) faults.push('unknown ' + colourOf(other));
                      other.remove(); picker.remove();
                      return faults.filter(Boolean).join('; ');
                    })()
                    """;
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    (await ValueAsync(page, Colours)).Should().BeEmpty(theme);
                }
            }
        );
    }

    [Fact]
    public async Task BlueprintsAreReorderedByDragOrArrowsAndKeepTheirOrderEverywhere()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            var origin = new TaskOrigin("test", "test", "fixture");
            tasks.ImportBlueprints(
                [new("M1", "One"), new("M2", "Two"), new("M3", "Three")],
                "test",
                cancellationToken: Token
            );
            foreach (
                var (id, blueprint) in new[]
                {
                    ("UI-0001", "M1"),
                    ("UI-0002", "M2"),
                    ("UI-0003", "M3"),
                    ("UI-0004", "M4"),
                    ("UI-0005", "M5"),
                }
            )
            {
                tasks.Create(
                    new TaskDefinition
                    {
                        Id = id,
                        Title = "Task of " + blueprint,
                        Blueprint = blueprint,
                    },
                    "",
                    origin,
                    Token
                );
            }

            // M4 and M5 hold only deferred tasks: they come after the others, in their own order
            var human = new TaskActor("human:fixture", Privileged: true);
            tasks.Defer("UI-0004", human, "later", Token);
            tasks.Defer("UI-0005", human, "later", Token);
        }

        const string Shown = "[...document.querySelectorAll('#main .mstone')].map(s => s.dataset.blueprint).join()";
        const string Drag = """
            window.drag = (from, to, after, type) => {
              const dt = new DataTransfer();
              from.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
              if (type && !dt.types.includes(type)) return 'no ' + type;
              const box = to.getBoundingClientRect();
              const at = { bubbles: true, cancelable: true, dataTransfer: dt, clientY: after ? box.bottom - 2 : box.top + 2 };
              to.dispatchEvent(new DragEvent('dragover', at));
              const marked = [...to.classList].filter(c => c.startsWith('bpdrop')).join();
              to.dispatchEvent(new DragEvent('drop', at));
              from.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: dt }));
              return marked;
            };
            """;
        string Section(string m) => $"document.querySelector('#main .mstone[data-blueprint={m}]')";
        await BrowsePagesAsync(
            executable,
            fixture,
            2,
            async (pages, server) =>
            {
                var page = pages[0];
                var id = UiWriteTests.ProjectId(fixture);
                var tasksView = server.Client.BaseAddress + "#/p/" + id + "/work/tasks";
                await page.CommandAsync("Page.navigate", new { url = tasksView });
                await page.WaitAsync(Shown + " === 'M1,M2,M3,M4,M5'");
                await page.EvaluateAsync(Drag);

                // dragged by its header above M1: the bar shows where it lands, the order is saved, no task moves
                (
                    await ValueAsync(
                        page,
                        $"drag({Section("M3")}.querySelector('.mhead'), {Section("M1")}, false, 'text/x-blueprint')"
                    )
                )
                    .Should()
                    .Be("bpdrop-before");
                await page.WaitAsync(Shown + " === 'M3,M1,M2,M4,M5'");
                using (var database = fixture.Database())
                {
                    new TaskStore(database).Get("UI-0003", Token).Definition.Blueprint.Should().Be("M3");
                    new TaskStore(database)
                        .Blueprints(Token)
                        .Select(b => $"{b.Definition.Id}@{b.Order}")
                        .Should()
                        .Equal("M3@0", "M1@1", "M2@2", "M4@3", "M5@4");
                }

                // the keyboard way: the arrows beside the header, the focus staying on the arrow
                (await ValueAsync(page, $"{Section("M3")}.querySelector('.bpmove-up').disabled"))
                    .Should()
                    .Be("True");
                await page.EvaluateAsync($"{Section("M3")}.querySelector('.bpmove-down').click()");
                await page.WaitAsync(Shown + " === 'M1,M3,M2,M4,M5'");
                await page.WaitAsync(
                    "document.activeElement && document.activeElement.getAttribute('aria-label') === 'Move down M3'"
                );
                // a deferred-only blueprint moved up stays among those, now first of them
                await page.EvaluateAsync($"{Section("M5")}.querySelector('.bpmove-up').click()");
                await page.WaitAsync(Shown + " === 'M1,M3,M2,M5,M4'");

                // a task dropped on a blueprint still moves there, and is no blueprint drag
                (
                    await ValueAsync(
                        page,
                        $"drag(document.querySelector('#task-UI-0001 .trow'), {Section("M2")}, true, 'text/x-task')"
                    )
                )
                    .Should()
                    .BeEmpty("a task drag shows no blueprint drop bar");
                await ProviderFixture.EventuallyAsync(() =>
                {
                    using var database = fixture.Database();
                    return new TaskStore(database).Get("UI-0001", Token).Definition.Blueprint is "M2";
                });
                await page.WaitAsync(Shown + " === 'M3,M2,M5,M4'");

                (
                    await ValueAsync(
                        page,
                        "fetch(location.pathname.replace(/\\/$/, '') + '/api/projects/"
                            + id
                            + "/blueprints').then(r => r.json())"
                            + ".then(j => j.blueprints.map(b => b.id + '@' + b.order).join())"
                    )
                )
                    .Should()
                    .Be("M1@0,M3@1,M2@2,M5@3,M4@4", "the blueprints API returns each one's order");

                // with one blueprint picked there is nothing to order
                await page.EvaluateAsync(
                    "(() => { const s = document.querySelector('select[aria-label=Blueprint]'); s.value = 'M2'; s.dispatchEvent(new Event('change')); })()"
                );
                await page.WaitAsync(Shown + " === 'M2'");
                (
                    await ValueAsync(
                        page,
                        "document.querySelectorAll('#main .bpmove, #main .mhead[draggable=true]').length"
                    )
                )
                    .Should()
                    .Be("0");

                // the same order after a reload, in another browser context and from a freshly started harness ui
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(Shown + " === 'M3,M2,M5,M4'");
                await using var restarted = await UiFixture.StartAsync(fixture);
                await pages[1]
                    .CommandAsync(
                        "Page.navigate",
                        new { url = restarted.Client.BaseAddress + "#/p/" + id + "/work/tasks" }
                    );
                await pages[1].WaitAsync(Shown + " === 'M3,M2,M5,M4'");
            }
        );
    }

    [Fact]
    public async Task AWriteRedrawsWithoutWaitingForTheStacksWhichFillTheirBadgesInPlaceWhenTheyArrive()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            tasks.ImportBlueprints([new("M1", "One"), new("M2", "Two")], "test", cancellationToken: Token);
            foreach (var (id, blueprint) in new[] { ("UI-0001", "M1"), ("UI-0002", "M2") })
            {
                tasks.Create(
                    new TaskDefinition
                    {
                        Id = id,
                        Title = "Task of " + blueprint,
                        Blueprint = blueprint,
                    },
                    "",
                    new TaskOrigin("test", "test", "fixture"),
                    Token
                );
            }
        }

        const string Shown = "[...document.querySelectorAll('#main .mstone')].map(s => s.dataset.blueprint).join()";
        // every /api/stacks from now on is held until release() answers it with one stack for the project
        const string Hold = """
            window.heldStacks = [];
            window.realFetch = window.realFetch || window.fetch;
            window.fetch = (url, ...rest) => String(url).includes('/api/stacks')
              ? new Promise((resolve) => window.heldStacks.push(resolve))
              : window.realFetch(url, ...rest);
            window.release = (id) => {
              const body = JSON.stringify({ [id]: { language: 'C#', runtime: { name: '.NET', version: '10.0', major: '10' }, frameworks: [], badge: 'C# · .NET 10' } });
              window.heldStacks.splice(0).forEach((r) => r(new Response(body, { headers: { 'Content-Type': 'application/json' } })));
            };
            'held';
            """;
        await BrowsePagesAsync(
            executable,
            fixture,
            1,
            async (pages, server) =>
            {
                var page = pages[0];
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/tasks" }
                );
                await page.WaitAsync(Shown + " === 'M1,M2'");
                await page.EvaluateAsync(Hold);

                // the write's redraw does not wait for the held stacks, and asks for them again
                await page.EvaluateAsync(
                    "document.querySelector('#main .mstone[data-blueprint=M1] .bpmove-down').click()"
                );
                await page.WaitAsync(Shown + " === 'M2,M1'");
                (await ValueAsync(page, "window.heldStacks.length > 0")).Should().Be("True");

                // when they arrive the badge is drawn in place: the sections are not drawn again
                await page.EvaluateAsync("window.section = document.querySelector('#main .mstone[data-blueprint=M2]')");
                await page.EvaluateAsync($"window.release('{id}')");
                await page.WaitAsync(
                    "[...document.querySelectorAll('.metaline .stackbadge')].map(b => b.getAttribute('aria-label')).join() === 'C# · .NET 10'"
                );
                (await ValueAsync(page, "window.section.isConnected")).Should().Be("True");

                // a failing /api/stacks breaks nothing and keeps the badge already drawn
                await page.EvaluateAsync(
                    "window.fetch = (url, ...rest) => String(url).includes('/api/stacks') ? Promise.reject(new Error('down')) : window.realFetch(url, ...rest)"
                );
                await page.EvaluateAsync(
                    "document.querySelector('#main .mstone[data-blueprint=M2] .bpmove-down').click()"
                );
                await page.WaitAsync(Shown + " === 'M1,M2'");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('.metaline .stackbadge')].map(b => b.getAttribute('aria-label')).join()"
                    )
                )
                    .Should()
                    .Be("C# · .NET 10");
            }
        );
    }

    [Fact]
    public async Task BlueprintCardsShowASummaryWhosePartsOpenOnTheirOwnAndStayOpenAcrossRedraws()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        var origin = new TaskOrigin("test", "test", "fixture");
        var words = string.Join(" ", Enumerable.Repeat("a goal that keeps going well past two lines", 12));
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            tasks.ImportBlueprints(
                [
                    new("M1", "Long", words, ["one", "two", "three", "four", "five"], "Notes " + words),
                    new("M2", "Short", "Brief.", ["only", "two"], "- first\n- second\n- third\n- fourth"),
                ],
                "test",
                cancellationToken: Token
            );
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0001",
                    Title = "In M1",
                    Blueprint = "M1",
                },
                "",
                origin,
                Token
            );
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0002",
                    Title = "In M2",
                    Blueprint = "M2",
                },
                "",
                origin,
                Token
            );
        }

        string Card(string m) => $"document.querySelector('#main .mcard[data-blueprint=\"{m}\"]')";
        // per part: visible lines (text) or entries (list), the badge, the toggle's text and aria-expanded
        string State(string m) =>
            $$"""
                (() => {
                  const c = {{Card(m)}};
                  const lines = (e) => { const cs = getComputedStyle(e); return Math.round((e.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom)) / parseFloat(cs.lineHeight)); };
                  const more = (part) => { const b = part.querySelector('.mcmore'); return !b || b.hidden ? '-' : b.textContent + '/' + b.getAttribute('aria-expanded'); };
                  const text = (part) => part ? lines(part.querySelector('p')) + 'l ' + more(part) : 'none';
                  const list = (part) => part ? part.querySelector('.mcbadge').textContent + ' ' + [...part.querySelectorAll('li')].filter(l => l.offsetParent).length + 'v ' + more(part) : 'none';
                  const goal = c.querySelector('.mcgoal')?.closest('.mcpart');
                  const note = c.querySelector('.mcnote')?.closest('.mcpart') ;
                  return ['goal ' + text(goal), 'exit ' + list(c.querySelector('.mcexit:not(.mcnotes)')),
                    'notes ' + (note ? text(note) : list(c.querySelector('.mcnotes')))].join(' | ');
                })()
                """;
        string Toggle(string m, string selector) =>
            $"{Card(m)}.querySelector('{selector}').closest('.mcpart, .mcexit').querySelector('.mcmore')";
        const string M1Collapsed = "goal 2l more/false | exit 5 3v Show all 5/false | notes 2l more/false";
        const string M2Collapsed = "goal 1l - | exit 2 2v - | notes 4 3v Show all 4/false";

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/blueprints" }
                );
                await page.WaitAsync(
                    $"!!{Card("M2")} && {Card("M1")}.querySelector('.mcgoal + .mcmore:not([hidden])') !== null"
                );
                // the summary: two lines of goal and notes, three entries under their count; no toggle when it all fits
                (await ValueAsync(page, State("M1")))
                    .Should()
                    .Be(M1Collapsed);
                (await ValueAsync(page, State("M2"))).Should().Be(M2Collapsed);
                (
                    await ValueAsync(
                        page,
                        $$"""
                        (() => {
                          const b = {{Toggle("M1", ".mcgoal")}}, p = {{Card("M1")}}.querySelector('.mcgoal');
                          return [b.tagName, b.type, b.tabIndex, b.getAttribute('aria-controls') === p.id, getComputedStyle(p).webkitLineClamp].join();
                        })()
                        """
                    )
                ).Should().Be("BUTTON,button,0,true,2");

                // each part opens on its own
                await page.EvaluateAsync($"{Toggle("M1", ".mcgoal")}.click()");
                (await ValueAsync(page, State("M1")))
                    .Should()
                    .MatchRegex(@"^goal ([3-9]|\d\d)l less/true \| exit 5 3v Show all 5/false \| notes 2l more/false$");
                await page.EvaluateAsync($"{Toggle("M1", "li")}.click()");
                await page.EvaluateAsync($"{Toggle("M2", ".mcnotes li")}.click()");
                (await ValueAsync(page, State("M2")))
                    .Should()
                    .Be("goal 1l - | exit 2 2v - | notes 4 4v Show less/true");
                var opened = await ValueAsync(page, State("M1"));
                opened.Should().Contain("exit 5 5v Show less/true").And.Contain("notes 2l more/false");

                // the toggle's focus ring, and its text readable in light and dark
                await ForcePseudoStateAsync(page, "#main .mcmore:not([hidden])", ["focus-visible"]);
                (
                    await ValueAsync(
                        page,
                        "getComputedStyle(document.querySelector('#main .mcmore:not([hidden])')).outlineStyle"
                    )
                )
                    .Should()
                    .Be("solid");
                await ForcePseudoStateAsync(page, "#main .mcmore:not([hidden])", []);
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    (await ValueAsync(page, ButtonContrast("#main .mcmore:not([hidden])"))).Should().Be("ok", theme);
                }

                await page.CommandAsync("Emulation.setEmulatedMedia", new { features = Array.Empty<object>() });

                // a live refresh and a reorder redraw the cards: what was open stays open
                using (var database = fixture.Database())
                {
                    new TaskStore(database).Create(
                        new TaskDefinition
                        {
                            Id = "UI-0003",
                            Title = "Also in M1",
                            Blueprint = "M1",
                        },
                        "",
                        origin,
                        Token
                    );
                }

                await page.WaitAsync($"{Card("M1")}.querySelector('.mclink').textContent.includes('2 tasks')");
                (await ValueAsync(page, State("M1"))).Should().Be(opened);
                await page.EvaluateAsync($"{Card("M2")}.querySelector('.bpmove-up').click()");
                await page.WaitAsync(
                    "[...document.querySelectorAll('#main .mcard')].map(c => c.dataset.blueprint).join().startsWith('M2,M1')"
                );
                (await ValueAsync(page, State("M1"))).Should().Be(opened);
                (await ValueAsync(page, State("M2")))
                    .Should()
                    .Be("goal 1l - | exit 2 2v - | notes 4 4v Show less/true");

                // closed again, it is the summary again
                await page.EvaluateAsync($"{Toggle("M1", ".mcgoal")}.click()");
                await page.EvaluateAsync($"{Toggle("M1", "li")}.click()");
                (await ValueAsync(page, State("M1"))).Should().Be(M1Collapsed);

                // on a phone the card holds its badges and toggles, and nothing scrolls sideways
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 390,
                        height = 900,
                        deviceScaleFactor = 1,
                        mobile = true,
                    }
                );
                await page.WaitAsync("innerWidth <= 400");
                (
                    await ValueAsync(
                        page,
                        """
                        (() => {
                          const inside = [...document.querySelectorAll('#main .mcard')].every((c) => {
                            const r = c.getBoundingClientRect();
                            return [...c.querySelectorAll('.mcbadge, .mcmore:not([hidden])')].every((e) => { const b = e.getBoundingClientRect(); return b.left >= r.left - 1 && b.right <= r.right + 1; });
                          });
                          return [inside, document.documentElement.scrollWidth <= innerWidth].join();
                        })()
                        """
                    )
                ).Should().Be("true,true");
                await page.CommandAsync("Emulation.clearDeviceMetricsOverride", new { });

                // a fresh page load starts with the summary
                await page.EvaluateAsync($"{Toggle("M2", ".mcnotes li")}.click()");
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(
                    $"!!{Card("M2")} && {Card("M1")}.querySelector('.mcgoal + .mcmore:not([hidden])') !== null"
                );
                (await ValueAsync(page, State("M2"))).Should().Be(M2Collapsed);
            }
        );
    }

    [Fact]
    public async Task TaskSectionGoalsAreCutToTwoLinesAndOpenOnTheirOwn()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        var origin = new TaskOrigin("test", "test", "fixture");
        var words = string.Join(" ", Enumerable.Repeat("a goal that keeps going well past two lines", 12));
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            tasks.ImportBlueprints(
                [new("M1", "Long", words), new("M2", "Short", "Brief.")],
                "test",
                cancellationToken: Token
            );
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0001",
                    Title = "In M1",
                    Blueprint = "M1",
                },
                "",
                origin,
                Token
            );
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0002",
                    Title = "In M2",
                    Blueprint = "M2",
                },
                "",
                origin,
                Token
            );
        }

        string Section(string m) => $"document.querySelector('#main .mstone[data-blueprint=\"{m}\"]')";
        // the goal's visible lines, its toggle's text and aria-expanded, whether the section is open
        string State(string m) =>
            $$"""
                (() => {
                  const s = {{Section(m)}}, g = s.querySelector('.mgoal');
                  if (!g) return 'folded';
                  const b = g.parentElement.querySelector('.mcmore');
                  const lines = Math.round(g.clientHeight / parseFloat(getComputedStyle(g).lineHeight));
                  return lines + 'l ' + (b.hidden ? '-' : b.textContent + '/' + b.getAttribute('aria-expanded'));
                })()
                """;
        string Toggle(string m) => $"{Section(m)}.querySelector('.mgoal').parentElement.querySelector('.mcmore')";
        string Ready() => $"!!{Section("M2")} && !!{Section("M1")}?.querySelector('.mgoal + .mcmore:not([hidden])')";

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var work = server.Client.BaseAddress + "#/p/" + UiWriteTests.ProjectId(fixture) + "/work/";
                await page.CommandAsync("Page.navigate", new { url = work + "tasks" });
                await page.WaitAsync(Ready());
                (await ValueAsync(page, State("M1"))).Should().Be("2l more/false");
                (await ValueAsync(page, State("M2"))).Should().Be("1l -");
                (
                    await ValueAsync(
                        page,
                        $$"""
                        (() => {
                          const b = {{Toggle("M1")}}, g = {{Section("M1")}}.querySelector('.mgoal');
                          return [b.tagName, b.type, b.tabIndex, b.getAttribute('aria-controls') === g.id, getComputedStyle(g).webkitLineClamp].join();
                        })()
                        """
                    )
                ).Should().Be("BUTTON,button,0,true,2");

                // opening it neither folds the section nor touches the other one
                await page.EvaluateAsync($"{Toggle("M1")}.click()");
                var opened = await ValueAsync(page, State("M1"));
                opened.Should().MatchRegex(@"^([3-9]|\d\d)l less/true$");
                (await ValueAsync(page, $"{Section("M1")}.querySelector('.mhead').getAttribute('aria-expanded')"))
                    .Should()
                    .Be("true");
                (await ValueAsync(page, State("M2"))).Should().Be("1l -");

                // focus ring and contrast in both themes
                await ForcePseudoStateAsync(page, "#main .mstone .mcmore:not([hidden])", ["focus-visible"]);
                (
                    await ValueAsync(
                        page,
                        "getComputedStyle(document.querySelector('#main .mstone .mcmore:not([hidden])')).outlineStyle"
                    )
                )
                    .Should()
                    .Be("solid");
                await ForcePseudoStateAsync(page, "#main .mstone .mcmore:not([hidden])", []);
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    (await ValueAsync(page, ButtonContrast("#main .mstone .mcmore:not([hidden])")))
                        .Should()
                        .Be("ok", theme);
                }

                await page.CommandAsync("Emulation.setEmulatedMedia", new { features = Array.Empty<object>() });

                // kept across a live refresh, a reorder, a filter change and folding the section
                using (var database = fixture.Database())
                {
                    new TaskStore(database).Create(
                        new TaskDefinition
                        {
                            Id = "UI-0003",
                            Title = "Also in M1",
                            Blueprint = "M1",
                        },
                        "",
                        origin,
                        Token
                    );
                }

                await page.WaitAsync(
                    $"{Section("M1")}.textContent.includes('UI-0003')"
                );
                (await ValueAsync(page, State("M1"))).Should().Be(opened);
                await page.EvaluateAsync($"{Section("M2")}.querySelector('.bpmove-up').click()");
                await page.WaitAsync(
                    "[...document.querySelectorAll('#main .mstone')].map(s => s.dataset.blueprint).join().startsWith('M2,M1')"
                );
                (await ValueAsync(page, State("M1"))).Should().Be(opened);
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('#main .chip')].find(c => c.textContent.includes('done')).click()"
                );
                (await ValueAsync(page, State("M1"))).Should().Be(opened);
                await page.EvaluateAsync($"{Section("M1")}.querySelector('.mhead').click()");
                (await ValueAsync(page, State("M1"))).Should().Be("folded");
                await page.EvaluateAsync($"{Section("M1")}.querySelector('.mhead').click()");
                await page.WaitAsync($"{Toggle("M1")}.getAttribute('aria-expanded') === 'true'");
                (await ValueAsync(page, State("M1"))).Should().Be(opened);

                // on a phone the goal and its toggle stay inside the section, nothing scrolls sideways
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 390,
                        height = 900,
                        deviceScaleFactor = 1,
                        mobile = true,
                    }
                );
                await page.WaitAsync("innerWidth <= 400");
                (
                    await ValueAsync(
                        page,
                        $$"""
                        (() => {
                          const s = {{Section("M1")}}.getBoundingClientRect();
                          const inside = [...{{Section(
                            "M1"
                        )}}.querySelectorAll('.mgoal, .mcmore')].every((e) => { const b = e.getBoundingClientRect(); return b.left >= s.left - 1 && b.right <= s.right + 1; });
                          return [inside, document.documentElement.scrollWidth <= innerWidth].join();
                        })()
                        """
                    )
                ).Should().Be("true,true");
                await page.CommandAsync("Emulation.clearDeviceMetricsOverride", new { });

                // the Blueprints card keeps its own state: still the summary there
                await page.CommandAsync("Page.navigate", new { url = work + "blueprints" });
                await page.WaitAsync(
                    "!!document.querySelector('#main .mcard[data-blueprint=\"M1\"] .mcgoal + .mcmore:not([hidden])')"
                );
                (
                    await ValueAsync(
                        page,
                        "document.querySelector('#main .mcard[data-blueprint=\"M1\"] .mcgoal + .mcmore').getAttribute('aria-expanded')"
                    )
                )
                    .Should()
                    .Be("false");

                // a fresh page load starts collapsed
                await page.CommandAsync("Page.navigate", new { url = work + "tasks" });
                await page.WaitAsync("location.hash.endsWith('/work/tasks') && !!document.querySelector('#main .mstone')");
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(Ready());
                (await ValueAsync(page, State("M1"))).Should().Be("2l more/false");
            }
        );
    }

    [Fact]
    public async Task BlueprintCardsAreReorderedAsTheTasksSectionsAreAndShareTheirOrder()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            var origin = new TaskOrigin("test", "test", "fixture");
            tasks.ImportBlueprints(
                [new("M1", "One"), new("M2", "Two"), new("M3", "Three")],
                "test",
                cancellationToken: Token
            );
            // M2's only task is done: the Open filter hides its card
            tasks.Import(
                [
                    new TaskDefinition
                    {
                        Id = "UI-0001",
                        Title = "Open in M1",
                        Blueprint = "M1",
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0002",
                        Title = "Done in M2",
                        Blueprint = "M2",
                        Status = "done",
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0003",
                        Title = "Open in M3",
                        Blueprint = "M3",
                    },
                ],
                "test",
                cancellationToken: Token
            );
            tasks.Create(new TaskDefinition { Id = "UI-0004", Title = "No blueprint" }, "", origin, Token);
        }

        const string Cards = "[...document.querySelectorAll('#main .mcard')].map(c => c.dataset.blueprint).join()";
        const string Sections = "[...document.querySelectorAll('#main .mstone')].map(s => s.dataset.blueprint).join()";
        string Card(string m) => $"document.querySelector('#main .mcard[data-blueprint=\"{m}\"]')";
        string Saved()
        {
            using var database = fixture.Database();
            return string.Join(
                ",",
                new TaskStore(database).Blueprints(Token).Select(b => $"{b.Definition.Id}@{b.Order}")
            );
        }

        await BrowsePagesAsync(
            executable,
            fixture,
            2,
            async (pages, server) =>
            {
                var page = pages[0];
                var id = UiWriteTests.ProjectId(fixture);
                var work = server.Client.BaseAddress + "#/p/" + id + "/work/";
                await page.CommandAsync("Page.navigate", new { url = work + "blueprints" });
                await page.WaitAsync(Cards + " === 'M1,M3,—'");
                await page.EvaluateAsync(
                    """
                    window.drag = (from, to, after) => {
                      const dt = new DataTransfer();
                      from.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
                      const box = to.getBoundingClientRect();
                      const at = { bubbles: true, cancelable: true, dataTransfer: dt, clientX: after ? box.right - 2 : box.left + 2, clientY: after ? box.bottom - 2 : box.top + 2 };
                      to.dispatchEvent(new DragEvent('dragover', at));
                      const marked = [...to.classList].filter(c => c.startsWith('bp')).join();
                      to.dispatchEvent(new DragEvent('drop', at));
                      from.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: dt }));
                      return marked;
                    };
                    """
                );
                // the card without a blueprint is not ordered
                (await ValueAsync(page, $"!!{Card("—")}.querySelector('.bpmove') || {Card("—")}.draggable"))
                    .Should()
                    .Be("False");
                // a card's id in its blueprint's coloured frame, on its own in the top row (no .mid chip around
                // it), centred with the title wide and on a phone; none on the card without a blueprint
                var frame = $$"""
                    (() => {
                      const f = {{Card("M1")}}.querySelector('.mctop > .bpframe');
                      const mid = (e) => { const r = e.getBoundingClientRect(); return (r.top + r.bottom) / 2; };
                      return [f?.className, !!f?.closest('.mid') || !!{{Card("M1")}}.querySelector('.mid'),
                        Math.abs(mid(f) - mid({{Card("M1")}}.querySelector('.mctop h3'))) < 3, !!{{Card(
                        "—"
                    )}}.querySelector('.bpframe')].join();
                    })()
                    """;
                (await ValueAsync(page, frame)).Should().Be("bpframe bpc-teal,false,true,false");
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 390,
                        height = 900,
                        deviceScaleFactor = 1,
                        mobile = true,
                    }
                );
                await page.WaitAsync("innerWidth <= 400");
                (await ValueAsync(page, frame)).Should().Be("bpframe bpc-teal,false,true,false", "at 390 px");
                await page.CommandAsync("Emulation.clearDeviceMetricsOverride", new { });
                await page.WaitAsync("innerWidth > 400");
                // the bar where a card would land shows in light and dark alike (the drag is not dropped)
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    (
                        await ValueAsync(
                            page,
                            $$"""
                            (() => {
                              const from = {{Card("M3")}}, to = {{Card("M1")}}, dt = new DataTransfer();
                              from.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt }));
                              const box = to.getBoundingClientRect();
                              to.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt, clientX: box.left + 2, clientY: box.top + 2 }));
                              const probe = document.createElement('span'); probe.style.color = 'var(--accent)'; document.body.append(probe);
                              const accent = getComputedStyle(probe).color; probe.remove();
                              const shadow = getComputedStyle(to).boxShadow;
                              from.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: dt }));
                              return shadow.includes(accent) ? 'bar' : 'no bar: ' + shadow;
                            })()
                            """
                        )
                    ).Should().Be("bar", theme);
                }

                await page.CommandAsync("Emulation.setEmulatedMedia", new { features = Array.Empty<object>() });

                // dragged before M1 under the Open filter: hidden M2 keeps its place after M1
                (await ValueAsync(page, $"drag({Card("M3")}, {Card("M1")}, false)"))
                    .Should()
                    .StartWith("bpdrop-before");
                await page.WaitAsync(Cards + " === 'M3,M1,—'");
                Saved().Should().Be("M3@0,M1@1,M2@2");

                // the arrows: moved to the end, the focus stays on the card's arrow that still works
                await page.EvaluateAsync($"{Card("M3")}.querySelector('.bpmove-down').click()");
                await page.WaitAsync(Cards + " === 'M1,M3,—'");
                await page.WaitAsync(
                    "document.activeElement && document.activeElement.getAttribute('aria-label') === 'Move up M3'"
                );
                Saved().Should().Be("M1@0,M3@1,M2@2");

                // with every card shown, the done one moves among them too
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('#main .chip')].find(c => c.textContent.startsWith('All')).click()"
                );
                await page.WaitAsync(Cards + " === 'M1,M3,M2,—'");
                await page.EvaluateAsync($"{Card("M2")}.querySelector('.bpmove-up').click()");
                await page.WaitAsync(Cards + " === 'M1,M2,M3,—'");
                Saved().Should().Be("M1@0,M2@1,M3@2");

                // a card's own buttons still answer a click
                await page.EvaluateAsync($"{Card("M1")}.querySelector('.mcedit').click()");
                await page.WaitAsync("!!document.querySelector('dialog[open]')");
                await page.EvaluateAsync("document.querySelector('dialog[open]').close()");

                // the Tasks view lists the sections in the same order, in this browser and another, from a new harness ui
                await page.EvaluateAsync($"location.hash = '#/p/{id}/work/tasks'");
                await page.WaitAsync(Sections + " === 'M1,M3,—'");
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(Sections + " === 'M1,M3,—'");
                await using var restarted = await UiFixture.StartAsync(fixture);
                await pages[1]
                    .CommandAsync(
                        "Page.navigate",
                        new { url = restarted.Client.BaseAddress + "#/p/" + id + "/work/blueprints" }
                    );
                await pages[1].WaitAsync(Cards + " === 'M1,M3,—'");

                // Show its tasks still opens the blueprint's tasks
                await pages[1].EvaluateAsync($"{Card("M3")}.querySelector('.mclink').click()");
                await pages[1].WaitAsync(Sections + " === 'M3'");
            }
        );
    }

    [Fact]
    public async Task TheBoardShowsEveryTaskInItsStatusColumnWithBlueprintColoursAndKeepsItsChoices()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            tasks.ImportBlueprints(
                [new("M1", "One"), new("M2", "Two, with a title far too long to fit on one card line", Color: "rose")],
                "test",
                cancellationToken: Token
            );
            tasks.Import(
                [
                    new TaskDefinition
                    {
                        Id = "UI-0001",
                        Title = "Zed fix",
                        Blueprint = "M1",
                        Type = "bug",
                        Priority = "high",
                        Acceptance = ["it works"],
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0002",
                        Title = "Later",
                        Blueprint = "M2",
                        // waits for UI-0001, so it stays in the backlog
                        DependsOn = ["UI-0001"],
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0003",
                        Title = "Just done",
                        Status = "done",
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0004",
                        Title = "Done long ago",
                        Status = "done",
                    },
                    new TaskDefinition
                    {
                        Id = "UI-0005",
                        Title = "Alpha docs",
                        Blueprint = "M2",
                        Type = "docs",
                        Priority = "low",
                    },
                ],
                "test",
                cancellationToken: Token
            );
        }

        fixture.Sql(
            "UPDATE tasks SET completed_at='"
                + DateTimeOffset
                    .UtcNow.AddDays(-10)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
                + "' WHERE id='UI-0004'"
        );
        const string Columns = """
            [...document.querySelectorAll('#main .bcol')].map(c => c.dataset.status + ':' + [...c.querySelectorAll('.bcard')].map(k => k.dataset.task).join('+')).join(' ')
            """;
        await BrowsePagesAsync(
            executable,
            fixture,
            2,
            async (pages, server) =>
            {
                var page = pages[0];
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/board" }
                );
                await page.WaitAsync("document.querySelectorAll('#main .bcol').length === 7");
                // the tab sits between Work and Agents, its text only like the others
                (await ValueAsync(page, "[...document.querySelectorAll('.tabs a')].map(a => a.textContent).join()"))
                    .Should()
                    .Contain("Work,Board,Agents");
                (await ValueAsync(page, "!!document.querySelector('.tabs a svg')")).Should().Be("False");

                // a column per status as work moves, blocked last, no deferred one; done 10 days ago hidden at 7 days
                (await ValueAsync(page, Columns))
                    .Should()
                    .Be("backlog:UI-0002 ready:UI-0001+UI-0005 claimed: in_progress: review: done:UI-0003 blocked:");
                (await ValueAsync(page, "document.querySelector('#main .bcol[data-status=ready] .bhead').textContent"))
                    .Should()
                    .Contain("2");

                // a card: type icon and id left, priority right, centred on one line; the title below; the
                // blueprint's title (not its id) framed in its colour on one line, cut with an ellipsis; none without one
                (
                    await ValueAsync(
                        page,
                        """
                        (() => {
                          const card = document.querySelector('.bcard[data-task=UI-0001]');
                          const c = card.querySelector('.bctop');
                          const parts = [...c.children].map(e => e.classList[0]).join('>');
                          const mid = (e) => { const r = e.getBoundingClientRect(); return (r.top + r.bottom) / 2; };
                          const centred = [...c.children].every(e => Math.abs(mid(e) - mid(c)) < 2);
                          const right = Math.abs(c.querySelector('.prio').getBoundingClientRect().right - c.getBoundingClientRect().right) < 2;
                          const lines = [...card.children].map(e => e.classList[0]).join('>');
                          const frame = card.querySelector('.bpframe');
                          const probe = document.createElement('span'); probe.style.color = 'var(--bp-teal)'; document.body.append(probe);
                          const teal = getComputedStyle(probe).color; probe.remove();
                          const long = document.querySelector('.bcard[data-task=UI-0005] .bpframe');
                          const t = long.querySelector('.bptitle');
                          const oneLine = long.getBoundingClientRect().height < 2 * parseFloat(getComputedStyle(long).lineHeight) + 4;
                          return [parts, centred, right, lines, frame.textContent, frame.getAttribute('aria-label'),
                            getComputedStyle(frame).borderTopColor === teal && getComputedStyle(frame).color === teal,
                            oneLine && t.scrollWidth > t.clientWidth && getComputedStyle(t).textOverflow === 'ellipsis',
                            !!document.querySelector('.bcard[data-task=UI-0003] .bcbp'), c.querySelector('.ttype').getAttribute('aria-label')].join('|');
                        })()
                        """
                    )
                )
                    .Should()
                    .Be("ttype>linkbtn>prio|true|true|bctop>bctitle>bcbp|One|Blueprint M1 · One|true|true|false|bug");

                // narrower columns: seven fit where six 16rem ones did (1920 px wide), no sideways scroll
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 1920,
                        height = 1000,
                        deviceScaleFactor = 1,
                        mobile = false,
                    }
                );
                await page.WaitAsync("innerWidth === 1920");
                (
                    await ValueAsync(
                        page,
                        "(() => { const b = document.querySelector('#main .board'); const six = 6 * 16 * 16 + 5 * 12;"
                            + " return [b.clientWidth >= six, b.scrollWidth <= b.clientWidth, b.querySelectorAll('.bcol').length].join(); })()"
                    )
                )
                    .Should()
                    .Be("true,true,7");
                await page.CommandAsync("Emulation.clearDeviceMetricsOverride", new { });

                // the id opens the task as Work › Tasks does, with Edit and Delete; Escape closes it back to the board
                await page.EvaluateAsync("document.querySelector('.bcard[data-task=UI-0001] .tid').click()");
                await page.WaitAsync("!!document.querySelector('dialog[open] .tdetail')");
                (
                    await ValueAsync(
                        page,
                        "(() => { const d = document.querySelector('dialog[open]'); return [d.textContent.includes('it works'), d.contains(document.activeElement),"
                            + " [...d.querySelectorAll('.dlg-foot button')].map(b => b.textContent + (b.disabled ? '(off)' : '')).join()].join('|'); })()"
                    )
                )
                    .Should()
                    .Be("true|true|Delete…,Edit…,Close");
                // the detail block inset on every side and rounded; Delete readable at rest and highlighted
                (
                    await ValueAsync(
                        page,
                        """
                        (() => {
                          const d = document.querySelector('dialog[open] .tdetail'), cs = getComputedStyle(d);
                          return [parseFloat(cs.paddingLeft) >= 12, cs.paddingLeft === cs.paddingRight, parseFloat(cs.borderTopLeftRadius) > 0].join();
                        })()
                        """
                    )
                )
                    .Should()
                    .Be("true,true,true");
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    foreach (var hover in new[] { false, true })
                    {
                        await ForcePseudoStateAsync(page, "dialog[open] .dlg-foot .danger", hover ? ["hover"] : []);
                        // buttons ease their background in: measure once it has settled
                        await page.EvaluateAsync(
                            "Promise.all(document.querySelector('dialog[open] .dlg-foot .danger').getAnimations().map(a => a.finished))"
                                + ".then(() => new Promise(r => setTimeout(r, 50)))"
                        );
                        (await ValueAsync(page, ButtonContrast("dialog[open] .dlg-foot .danger")))
                            .Should()
                            .Be("ok", $"Delete in {theme}{(hover ? ", highlighted" : "")}");
                    }
                }

                await ForcePseudoStateAsync(page, "dialog[open] .dlg-foot .danger", []);
                await page.CommandAsync("Emulation.setEmulatedMedia", new { features = Array.Empty<object>() });
                await page.CommandAsync(
                    "Input.dispatchKeyEvent",
                    new
                    {
                        type = "keyDown",
                        key = "Escape",
                        code = "Escape",
                        windowsVirtualKeyCode = 27,
                    }
                );
                await page.WaitAsync("!document.querySelector('dialog[open]') && location.hash.endsWith('/board')");

                // sort, done-age and blueprint filter, each saved for the project on this machine
                async Task Choose(string label, string value) =>
                    await page.EvaluateAsync(
                        $"(() => {{ const s = document.querySelector('select[aria-label=\"{label}\"]'); s.value = '{value}'; s.dispatchEvent(new Event('change')); }})()"
                    );
                await Choose("Sort by:", "name");
                await page.WaitAsync(Columns + ".includes('ready:UI-0005+UI-0001')");
                await Choose("Hide done tasks older than:", "14");
                await page.WaitAsync(Columns + ".includes('done:UI-0004+UI-0003')");
                await Choose("Blueprint:", "M2");
                await page.WaitAsync(
                    Columns + " === 'backlog:UI-0002 ready:UI-0005 claimed: in_progress: review: done: blocked:'"
                );
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(
                    Columns + " === 'backlog:UI-0002 ready:UI-0005 claimed: in_progress: review: done: blocked:'"
                );
                await using var restarted = await UiFixture.StartAsync(fixture);
                await pages[1]
                    .CommandAsync("Page.navigate", new { url = restarted.Client.BaseAddress + "#/p/" + id + "/board" });
                await pages[1].WaitAsync("document.querySelectorAll('#main .bcol').length === 7");
                (
                    await ValueAsync(
                        pages[1],
                        "['Sort by:', 'Hide done tasks older than:', 'Blueprint:'].map(l => document.querySelector(`select[aria-label=\"${l}\"]`).value).join()"
                    )
                )
                    .Should()
                    .Be("name,14,M2", "the choices are kept on the machine, not in the browser");

                // Machine › Board sets the defaults for projects without their own
                await page.EvaluateAsync("location.hash = '#/machine'");
                await page.WaitAsync("!!document.querySelector('select[aria-label=\"Sort cards by\"]')");
                await page.EvaluateAsync(
                    "(() => { const s = document.querySelector('select[aria-label=\"Sort cards by\"]'); s.value = 'changed'; s.dispatchEvent(new Event('change')); })()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    File.Exists(Path.Combine(fixture.Global, "ui.json"))
                    && File.ReadAllText(Path.Combine(fixture.Global, "ui.json"))
                        .Contains("\"changed\"", StringComparison.Ordinal)
                );

                // Work › Tasks frames each blueprint section as the cards do; the colour is picked from swatches
                await page.EvaluateAsync($"location.hash = '#/p/{id}/work/tasks'");
                await page.WaitAsync("!!document.querySelector('#main .mstone[data-blueprint=M2] .mhead .bpframe')");
                await page.EvaluateAsync($"location.hash = '#/p/{id}/work/blueprints'");
                await page.WaitAsync("!!document.querySelector('#main .mcard[data-blueprint=M2] .mcedit')");
                await page.EvaluateAsync("document.querySelector('#main .mcard[data-blueprint=M2] .mcedit').click()");
                await page.WaitAsync("document.querySelectorAll('dialog[open] .bpswatch').length === 16");
                (
                    await ValueAsync(
                        page,
                        "document.querySelector('dialog[open] .bpswatch[aria-checked=true]').getAttribute('aria-label')"
                    )
                )
                    .Should()
                    .Be("rose");
                await page.EvaluateAsync("document.querySelector('dialog[open] .bpswatch[aria-label=violet]').click()");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('dialog[open] button')].find(b => b.textContent === 'Save blueprint').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                {
                    using var database = fixture.Database();
                    return new TaskStore(database)
                        .Blueprints(Token)
                        .Single(b => b.Definition.Id == "M2")
                        .Definition.Color
                        is "violet";
                });
                (
                    await ValueAsync(
                        page,
                        $"fetch('/api/projects/{id}/blueprints/M2/edit', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json', 'X-Harness-UI': '1' }},"
                            + " body: JSON.stringify({ title: 'Two', color: 'mauve' }) }).then(r => r.status + ' ' + r.statusText)"
                    )
                )
                    .Should()
                    .StartWith("400", "an unknown colour is refused");

                // every colour readable on a card and a section header, light and dark
                const string Contrast = """
                    (() => {
                      const cv = document.createElement('canvas'); cv.width = cv.height = 1;
                      const g = cv.getContext('2d', { willReadFrequently: true });
                      const px = (...cs) => { g.clearRect(0, 0, 1, 1); for (const c of cs) { g.fillStyle = c; g.fillRect(0, 0, 1, 1); } return [...g.getImageData(0, 0, 1, 1).data].slice(0, 3); };
                      const lum = (rgb) => { const [r, gr, b] = rgb.map((v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * gr + 0.0722 * b; };
                      const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
                      const probe = (v) => { const d = document.createElement('div'); d.style.color = v; document.body.append(d); const c = getComputedStyle(d).color; d.remove(); return c; };
                      const surface = probe('var(--surface)');
                      const bgs = [px(surface), px(surface, probe('var(--surface-2)'))];
                      return BP_COLORS.map((c) => [c, Math.min(...bgs.map((b) => ratio(px(surface, probe(`var(--bp-${c})`)), b)))])
                        .filter(([, r]) => r < 4.5).map(([c, r]) => `${c} ${r.toFixed(2)}`).join(', ');
                    })()
                    """;
                foreach (var theme in new[] { "light", "dark" })
                {
                    await page.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    (await ValueAsync(page, Contrast)).Should().BeEmpty(theme);
                }

                (await ValueAsync(page, "BP_COLORS.join()")).Should().Be(string.Join(",", BlueprintColors.Names));
            }
        );

        // the CLI picks the colour too, and refuses one not in the set
        (await fixture.CliAsync("tasks", "blueprints", "edit", "M1", "--color", "mauve"))
            .Code.Should()
            .NotBe(0);
        (await fixture.CliAsync("tasks", "blueprints", "edit", "M1", "--color", "lime")).Code.Should().Be(0);
        using (var database = fixture.Database())
        {
            new TaskStore(database)
                .Blueprints(Token)
                .Single(b => b.Definition.Id == "M1")
                .Definition.Color.Should()
                .Be("lime");
        }
    }

    [Fact]
    public async Task BlueprintsAreCreatedEditedAndRemovedOnceConfirmedAskingAboutTheirTasks()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        // a word id, as an older harness or a backlog import left it: it still works, and no generated id follows it
        fixture.Sql(
            "INSERT INTO blueprints(id,title,goal,exit,notes,source,ordinal,updated_at) VALUES('EMPTY','Nothing in it','','[]','','import',0,'2026-01-01T00:00:00Z')"
        );
        static string Click(string text) =>
            $"[...document.querySelectorAll('button')].find(b => b.textContent === '{text}').click()";
        static string Card(string id) => $"document.querySelector('.mcard[data-blueprint=\"{id}\"]')";
        static string CardButton(string id, string action) =>
            $"{Card(id)}.querySelector('button[aria-label=\"{action} blueprint {id}\"]')";
        const string Dialog = "document.querySelector('dialog[open]')";
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/blueprints" }
                );
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'New blueprint…')"
                );
                await page.EvaluateAsync(Click("New blueprint…"));
                await page.WaitAsync($"{Dialog}?.querySelector('input[aria-label=Id]')?.value === 'BP-001'");
                await page.EvaluateAsync(
                    $"{Dialog}.querySelector('input[aria-label=Title]').value = 'From the browser'"
                );
                await page.EvaluateAsync(Click("Create blueprint"));
                await page.WaitAsync($"!!{Card("BP-001")}");
                tasks.Blueprints(Token).Select(b => b.Definition.Title).Should().Contain("From the browser");

                await page.EvaluateAsync($"{CardButton("BP-001", "Edit")}.click()");
                await page.WaitAsync($"!!{Dialog}?.querySelector('textarea[aria-label=Goal]')");
                (await ValueAsync(page, $"!!{Dialog}.querySelector('input[aria-label=Id]')"))
                    .Should()
                    .Be("False", "an edited blueprint keeps its id");
                await page.EvaluateAsync($"{Dialog}.querySelector('textarea[aria-label=Goal]').value = 'Edited goal'");
                await page.EvaluateAsync(Click("Save blueprint"));
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.Blueprints(Token).Single(b => b.Definition.Id == "BP-001").Definition.Goal is "Edited goal"
                );

                // no tasks: removed once confirmed, nothing else asked
                await page.WaitAsync($"!{Dialog}");
                await page.EvaluateAsync($"{CardButton("EMPTY", "Remove")}.click()");
                await page.WaitAsync($"!!{Dialog}");
                (await ValueAsync(page, $"!!{Dialog}.querySelector('input[type=radio]')")).Should().Be("False");
                tasks.Blueprints(Token).Should().HaveCount(2, "nothing is removed before it is confirmed");
                await page.EvaluateAsync(Click("Remove the blueprint"));
                await page.WaitAsync($"!{Card("EMPTY")}");
                tasks.Blueprints(Token).Select(b => b.Definition.Id).Should().Equal("BP-001");

                tasks.Create(
                    new TaskDefinition
                    {
                        Id = "UI-0001",
                        Title = "In the blueprint",
                        Blueprint = "BP-001",
                    },
                    "",
                    new("test", "test", "fixture"),
                    Token
                );
                await page.EvaluateAsync("location.reload()");
                await page.WaitAsync($"!!{Card("BP-001")}?.textContent.includes('1 task')");
                await page.EvaluateAsync($"{CardButton("BP-001", "Remove")}.click()");
                await page.WaitAsync($"!!{Dialog}?.querySelector('select[aria-label=\"Move to\"]')");
                (await ValueAsync(page, $"{Dialog}.textContent")).Should().Contain("It has 1 task.");
                // each radio button sits at the start of its line, its text right beside it
                (
                    await ValueAsync(
                        page,
                        """
                        [...document.querySelectorAll('dialog[open] label.choice')].every(l => {
                          const r = l.querySelector('input[type=radio]').getBoundingClientRect();
                          const t = l.querySelector('span').getBoundingClientRect();
                          return r.width < 30 && r.left - l.getBoundingClientRect().left < 4
                            && t.left - r.right < 16 && Math.abs((t.top + t.bottom) - (r.top + r.bottom)) < 12;
                        })
                        """
                    )
                )
                    .Should()
                    .Be("True");
                await page.EvaluateAsync(Click("Remove the blueprint"));
                await page.WaitAsync($"!{Card("BP-001")}");
                tasks.Get("UI-0001", Token).Definition.Blueprint.Should().BeEmpty("moved to no blueprint");
                tasks.Blueprints(Token).Should().BeEmpty();
            }
        );
    }

    [Fact]
    public async Task ProposedBlueprintIsCreatedFromTheInbox()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var proposal = tasks.ProposeBlueprint("", new("", "Agent idea", "Its goal"), "seen", "codex", Token);
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/inbox" }
                );
                await page.WaitAsync($"!!document.querySelector('[data-proposal=\"{proposal.Id}\"]')");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('h3.inboxhead')].some(x => x.textContent === 'Blueprints')"
                    )
                )
                    .Should()
                    .Be("True");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint…').click()"
                );
                await page.WaitAsync("document.querySelector('input[aria-label=Id]')?.value === 'BP-001'");
                (await ValueAsync(page, "document.querySelector('input[aria-label=Title]').value"))
                    .Should()
                    .Be("Agent idea");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.GetBlueprintProposal(proposal.Id, Token).Status is "approved"
                );
                tasks.Blueprints(Token).Single().Definition.Title.Should().Be("Agent idea");
            }
        );
    }

    [Fact]
    public async Task ATaskProposedForANewBlueprintJoinsItAndTheBlueprintIsMergedIntoTheOneItsTaskMade()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        const string Title = "Awasome Test 1";
        var blueprint = tasks.ProposeBlueprint(
            "",
            new("", Title, "Fast views", ["opens in a second"]),
            "",
            "codex",
            Token
        );
        TaskProposal Propose(string title) =>
            tasks.Propose(
                new TaskDefinition
                {
                    Title = title,
                    Goal = "Know where the time goes",
                    Acceptance = ["Measured"],
                    Blueprint = Title,
                },
                "seen",
                "codex",
                Token
            );
        // the first task was approved before its blueprint: it made BP-001, titled as proposed
        tasks.Approve(Propose("Measure the views").Id, null, "", "author", Token);
        var second = Propose("Cache the views");

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/inbox" }
                );
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'Create the task…')"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task…').click()"
                );
                await page.WaitAsync("!!document.querySelector('select[aria-label=Blueprint]')");
                (await ValueAsync(page, "document.querySelector('select[aria-label=Blueprint]').value"))
                    .Should()
                    .Be("BP-001", "the box shows the blueprint the title matches, by id");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task').click()"
                );
                await ProviderFixture.EventuallyAsync(() => tasks.GetProposal(second.Id, Token).Status is "approved");
                tasks.Get(tasks.GetProposal(second.Id, Token).Result, Token).Definition.Blueprint.Should().Be("BP-001");

                await page.EvaluateAsync("location.reload()");
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'Create the blueprint…')"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint…').click()"
                );
                await page.WaitAsync("!!document.querySelector('input[name^=bpsame-][value=merge]')");
                // the suggested id arrives a moment later; the browser holds the form until it has one
                await page.WaitAsync("document.querySelector('input[aria-label=Id]')?.value.length > 0");
                (await ValueAsync(page, "document.querySelector('form.aform').textContent"))
                    .Should()
                    .Contain("exists already (BP-001)");
                // each radio at the start of its line, its text beside it, centred with it; at a phone's width
                // the long merge label wraps beside its radio, not below it
                const string Beside = """
                    [...document.querySelectorAll('form.aform label.choice')].map(l => {
                      const r = l.querySelector('input[type=radio]').getBoundingClientRect();
                      const t = l.querySelector('span').getBoundingClientRect();
                      const ok = r.width < 30 && r.left - l.getBoundingClientRect().left < 4 && t.left > r.right
                        && t.left - r.right < 16 && t.top <= r.top && t.bottom >= r.bottom
                        && Math.abs((t.top + t.bottom) - (r.top + r.bottom)) < 12;
                      return ok ? 'beside' : `apart ${JSON.stringify([r.left, r.top, r.right, r.bottom, t.left, t.top, t.right, t.bottom])}`;
                    }).join()
                    """;
                (await ValueAsync(page, Beside)).Should().Be("beside,beside");
                await page.CommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    new
                    {
                        width = 390,
                        height = 900,
                        deviceScaleFactor = 1,
                        mobile = true,
                    }
                );
                await page.WaitAsync("innerWidth <= 400");
                (await ValueAsync(page, Beside)).Should().Be("beside,beside");
                (
                    await ValueAsync(
                        page,
                        "(() => { const t = document.querySelector('form.aform label.choice span'); return t.getBoundingClientRect().height > 1.5 * parseFloat(getComputedStyle(t).lineHeight || '20'); })()"
                    )
                )
                    .Should()
                    .Be("True", "the long label takes more than one line at 390 px");
                await page.CommandAsync("Emulation.clearDeviceMetricsOverride", new { });
                // asked, not guessed: without a choice the server refuses
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint').click()"
                );
                await page.WaitAsync("!document.querySelector('form.aform .error').hidden");
                tasks.GetBlueprintProposal(blueprint.Id, Token).Status.Should().Be("pending");
                await page.EvaluateAsync("document.querySelector('input[name^=bpsame-][value=merge]').click()");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.GetBlueprintProposal(blueprint.Id, Token).Status is "approved"
                );
                var merged = tasks.Blueprints(Token).Should().ContainSingle().Subject;
                (merged.Definition.Id, merged.Definition.Goal, merged.Total).Should().Be(("BP-001", "Fast views", 2));
            }
        );
    }

    [Fact]
    public async Task ProposedTaskIsApprovedFromTheInboxIntoItsBlueprint()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        tasks.CreateBlueprint(new("M1", "Metrics"), "test", Token);
        TaskProposal Propose(string blueprint) =>
            tasks.Propose(
                new TaskDefinition
                {
                    Title = "Into " + blueprint,
                    Goal = "Belongs to a blueprint",
                    Acceptance = ["Grouped"],
                    Blueprint = blueprint,
                },
                "seen",
                "codex",
                Token
            );

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/inbox" }
                );
                // A described blueprint, and one only named, which the form must not drop to none.
                foreach (var blueprint in new[] { "M1", "M9" })
                {
                    var proposal = Propose(blueprint);
                    await page.EvaluateAsync("location.reload()");
                    await page.WaitAsync(
                        "[...document.querySelectorAll('button')].some(b => b.textContent === 'Create the task…')"
                    );
                    await page.EvaluateAsync(
                        "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task…').click()"
                    );
                    await page.WaitAsync("!!document.querySelector('select[aria-label=Blueprint]')");
                    (await ValueAsync(page, "document.querySelector('select[aria-label=Blueprint]').value"))
                        .Should()
                        .Be(blueprint);
                    await page.EvaluateAsync(
                        "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task').click()"
                    );
                    await ProviderFixture.EventuallyAsync(() =>
                        tasks.GetProposal(proposal.Id, Token).Status is "approved"
                    );
                    tasks
                        .Get(tasks.GetProposal(proposal.Id, Token).Result, Token)
                        .Definition.Blueprint.Should()
                        .Be(blueprint);
                }
            }
        );
    }

    [Fact]
    public async Task ComplexityGroupShowsItsWorstLevelFiltersByLevelAndDrawsTheDistribution()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "example.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        // One problem, one warning and eight notes: ten findings, more than the eight shown at first.
        fixture.Write(
            "Router.cs",
            "namespace Example;\npublic class Router\n{\n"
                + string.Concat(
                    new[] { ("Huge", 60), ("Large", 30) }
                        .Concat(Enumerable.Range(11, 8).Select(value => ($"Note{value}", value)))
                        .Select(method =>
                            $"    public int {method.Item1}(int a)\n    {{\n"
                            + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", method.Item2 - 1))
                            + "        return a;\n    }\n"
                        )
                )
                + "}\n"
        );
        await fixture.CommitAsync("complex methods");
        await fixture.RunAsync();
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                const string Group =
                    "[...document.querySelectorAll('li.smell > details')].find(d => d.querySelector('summary').textContent.includes('paths through them'))";
                const string List = Group + ".querySelector('.smellsub')";
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + UiWriteTests.ProjectId(fixture) + "/architecture" }
                );
                await page.WaitAsync($"!!({Group})");
                (await ValueAsync(page, $"{Group}.querySelector('summary .pill').textContent"))
                    .Should()
                    .Be("✕problem", "the pill shows the worst level in the group");
                (await ValueAsync(page, $"{Group}.querySelector('summary').textContent")).Should().Contain("10 × ");
                await page.EvaluateAsync($"{Group}.querySelector('summary').click()");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li > details > summary').length"))
                    .Should()
                    .Be("9", "eight findings and the fold of the rest");
                (await ValueAsync(page, $"{List}.textContent")).Should().Contain("2 more, worth less");
                (await ValueAsync(page, $"{List}.querySelector('li summary').textContent"))
                    .Should()
                    .StartWith("Router.Huge has cyclomatic complexity 60");
                (
                    await ValueAsync(
                        page,
                        $"[...{Group}.querySelectorAll('.lvchips .chip')].map(c => c.textContent.trim()).join()"
                    )
                )
                    .Should()
                    .Be("✕ 1 problems,▲ 1 warnings,ⓘ 8 notes");

                await page.EvaluateAsync($"{Group}.querySelector('.lvchips .chip.lv-info').click()");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li').length")).Should().Be("2");
                (await ValueAsync(page, $"{Group}.open")).Should().Be("True", "filtering redraws only the list");

                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync($"!!({Group})");
                await page.EvaluateAsync($"{Group}.querySelector('summary').click()");
                (
                    await ValueAsync(
                        page,
                        $"{Group}.querySelector('.lvchips .chip.lv-info').getAttribute('aria-pressed')"
                    )
                )
                    .Should()
                    .Be("false", "the viewer's choice is kept");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li').length")).Should().Be("2");

                await page.EvaluateAsync(
                    "location.hash = '#/p/" + UiWriteTests.ProjectId(fixture) + "/architecture/complexity'"
                );
                await page.WaitAsync("!!document.querySelector('.cxchart')");
                (await ValueAsync(page, "document.querySelectorAll('.cxchart .cxth').length")).Should().Be("3");
                (await ValueAsync(page, "document.querySelector('.cxlang h4').textContent"))
                    .Should()
                    .Contain("10 functions, the most complex at 60");
                (await ValueAsync(page, "document.querySelector('.cxtop li b').textContent")).Should().Contain("Huge");
            }
        );
    }

    [Fact]
    public async Task RingsTrendKnowledgeIsAddedTelemetryToggledATaskDraggedAndAFixRun()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var origin = new TaskOrigin("test", "test", "fixture");
        tasks.CreateBlueprint(new("M1", "First"), "test", Token);
        tasks.Create(new TaskDefinition { Id = "UI-0001", Title = "Done today" }, "", origin, Token);
        var claim = tasks.Claim("UI-0001", new("claude"), cancellationToken: Token);
        tasks.Complete("UI-0001", new("claude", claim.Token), new("done"), Token);
        tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "To drag" }, "", origin, Token);
        tasks.Create(
            new TaskDefinition
            {
                Id = "UI-0003",
                Title = "Already in M1",
                Blueprint = "M1",
            },
            "",
            origin,
            Token
        );
        // Claude Code has run once on this machine: its user config exists, for harness install to add to
        var claude = Path.Combine(fixture.DirectoryRoot, "claude", ".claude.json");
        Directory.CreateDirectory(Path.GetDirectoryName(claude)!);
        await File.WriteAllTextAsync(claude, "{}", Token);
        static string Click(string text) =>
            $"[...document.querySelectorAll('button')].find(b => b.textContent === '{text}').click()";
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/overview" }
                );
                // a task done today: the tasks ring carries its trend
                await page.WaitAsync("!!document.querySelector('.ringlegend li[data-r=r-tasks] .rspark')");

                await page.EvaluateAsync("location.hash = '#/p/" + id + "/knowledge'");
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'Add knowledge')"
                );
                await page.EvaluateAsync(Click("Add knowledge"));
                await page.WaitAsync("!!document.querySelector('textarea[name=content]')");
                await page.EvaluateAsync(
                    "document.querySelector('textarea[name=content]').value = 'Added from the knowledge page'"
                );
                await page.EvaluateAsync(Click("Add"));
                await ProviderFixture.EventuallyAsync(() =>
                    new MemoryStore(database)
                        .Search(new() { Text = "Added from the knowledge page" }, Token)
                        .Any(m => m.Source.Type is "user")
                );

                await page.EvaluateAsync("location.hash = '#/machine'");
                await page.WaitAsync("!!document.querySelector('.telcard .switch')");
                await page.EvaluateAsync("document.querySelector('.telcard .switch').click()");
                await page.WaitAsync("!!document.querySelector('input[aria-label=\"Collector endpoint\"]')");
                await page.EvaluateAsync(
                    "document.querySelector('input[aria-label=\"Collector endpoint\"]').value = 'http://127.0.0.1:9/v1/report'"
                );
                await page.EvaluateAsync(Click("Turn on"));
                await page.WaitAsync(
                    "document.querySelector('.telcard .switch')?.getAttribute('aria-checked') === 'true'"
                );
                await page.EvaluateAsync("document.querySelector('.telcard .switch').click()");
                await page.WaitAsync(
                    "document.querySelector('.telcard .switch')?.getAttribute('aria-checked') === 'false'"
                );

                // a task dropped on another blueprint's group moves there
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/work/tasks'");
                await page.WaitAsync("!!document.querySelector('#task-UI-0002 .trow')");
                await page.EvaluateAsync(
                    """
                    (() => {
                      const dt = new DataTransfer();
                      const row = document.querySelector('#task-UI-0002 .trow');
                      const target = [...document.querySelectorAll('section.mstone')].find(s => s.querySelector('.mname')?.textContent.startsWith('M1'));
                      row.dispatchEvent(new DragEvent('dragstart', { dataTransfer: dt, bubbles: true }));
                      target.dispatchEvent(new DragEvent('dragover', { dataTransfer: dt, bubbles: true, cancelable: true }));
                      target.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
                    })()
                    """
                );
                await ProviderFixture.EventuallyAsync(() => tasks.Get("UI-0002", Token).Definition.Blueprint is "M1");

                // Health's Fix it runs the client check's own fix
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/health'");
                const string ClaudeFix =
                    "[...document.querySelectorAll('.chk')].find(c => c.querySelector('.cname')?.textContent.startsWith('claude-code'))?.querySelector('.fixbtn')";
                await page.WaitAsync($"!!{ClaudeFix}");
                await page.EvaluateAsync($"{ClaudeFix}.click()");
                // the install runs as its own process; its toast lasts two seconds, so the config is what is waited for
                bool Installed()
                {
                    try
                    {
                        return File.ReadAllText(claude).Contains("harness", StringComparison.Ordinal);
                    }
                    catch (IOException)
                    {
                        return false; // being written
                    }
                }
                await ProviderFixture.EventuallyAsync(Installed, TimeSpan.FromSeconds(60));
            }
        );
    }

    [Fact]
    public async Task ABrowserTestThatFailsMidPageLeavesNoBrowserProcessBehind()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        var profile = SharedBrowser.Edge.NewProfile();
        var running = 0;

        await FluentActions
            .Awaiting(() =>
                BrowseAsync(
                    executable,
                    fixture,
                    async (page, server) =>
                    {
                        await page.CommandAsync("Page.navigate", new { url = server.Client.BaseAddress + "#/" });
                        running = (await BrowserProcessesAsync(profile)).Count;
                        throw new InvalidOperationException("a check failed mid-page");
                    },
                    profile
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("a check failed mid-page");

        running.Should().BeGreaterThan(1, "the browser runs with its helper processes");
        (await BrowserProcessesAsync(profile)).Should().BeEmpty("ending the test ends the browser and every helper");
    }

    [Fact]
    public async Task SharedBrowserPagesShareNoStorageAndACrashedBrowserIsReplaced()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        await BrowsePagesAsync(
            executable,
            fixture,
            2,
            async (pages, server) =>
            {
                foreach (var page in pages)
                {
                    await page.CommandAsync("Page.navigate", new { url = server.Client.BaseAddress + "#/" });
                    await page.WaitAsync("document.readyState === 'complete'");
                }

                await pages[0]
                    .EvaluateAsync(
                        "localStorage.setItem('leak', 'yes'); sessionStorage.setItem('leak', 'yes'); "
                            + "document.cookie = 'leak=yes; path=/'"
                    );
                (await ValueAsync(pages[0], "localStorage.getItem('leak') + document.cookie"))
                    .Should()
                    .Be("yesleak=yes");
                (await ValueAsync(pages[1], "String(localStorage.getItem('leak')) + '|' + document.cookie"))
                    .Should()
                    .Be("null|", "a page in another browser context sees none of the first one's storage");
            }
        );

        var before = browser.ProcessId;
        before.Should().NotBeNull("the shared browser is still running after a test");
        await browser.CrashAsync();
        await BrowsePagesAsync(
            executable,
            fixture,
            1,
            async (pages, server) =>
            {
                await pages[0].CommandAsync("Page.navigate", new { url = server.Client.BaseAddress + "#/" });
                await pages[0].WaitAsync("document.readyState === 'complete'");
                (await ValueAsync(pages[0], "String(localStorage.getItem('leak'))"))
                    .Should()
                    .Be("null", "the next test starts clean in a fresh browser");
            }
        );
        browser.ProcessId.Should().NotBe(before, "a crashed shared browser is replaced for the next test");
    }

    [Fact]
    public async Task TaskIsDeletedFromItsRowAfterTheDialogSaysWhatGoes()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        tasks.Create(
            new TaskDefinition { Id = "UI-0001", Title = "To delete" },
            "",
            new("test", "test", "fixture"),
            Token
        );
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + UiWriteTests.ProjectId(fixture) + "/work/tasks" }
                );
                await page.WaitAsync("!!document.querySelector('#task-UI-0001 .rowdel')");
                await page.EvaluateAsync("document.querySelector('#task-UI-0001 .rowdel').click()");
                await page.WaitAsync("!!document.querySelector('dialog[open] .dchanges')");
                (await ValueAsync(page, "document.querySelector('dialog[open]').textContent"))
                    .Should()
                    .Contain("Delete UI-0001?")
                    .And.Contain("'harness tasks export' writes the deletion into the file");
                tasks.List(new(), Token).Should().ContainSingle("nothing goes before it is confirmed");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('dialog[open] button')].find(b => b.textContent === 'Delete task').click()"
                );
                await page.WaitAsync("!document.querySelector('#task-UI-0001')");
                tasks.List(new(), Token).Should().BeEmpty();
            }
        );
    }

    [Fact]
    public async Task ProjectKnowledgeIsSharedAndWithdrawnAndRemovedMemoriesAreAnsweredInTheInbox()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var memories = new MemoryStore(database);
        string Put(string content) =>
            memories
                .Put(
                    new()
                    {
                        Type = "decision",
                        Content = content,
                        Source = new("user", "test", "test"),
                    },
                    Token
                )
                .Memory!.Id;
        var local = Put("Knowledge cards can be shared");
        var kept = Put("Pagination uses cursors");
        var retired = Put("Exports run nightly at two");
        memories.SetShared(kept, SharedState.Removed, Token);
        memories.SetShared(retired, SharedState.Removed, Token);
        string Card(string id) =>
            $"[...document.querySelectorAll('article.mem')].find(a => a.querySelector('[title*=\"{id}\"]'))";
        string Click(string id, string label) =>
            $"[...{Card(id)}.querySelectorAll('button')].find(b => b.textContent === '{label}').click()";
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var project = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + project + "/knowledge" }
                );
                await page.WaitAsync($"!!{Card(local)}");
                (await ValueAsync(page, $"{Card(kept)}.querySelector('.shared.removed')?.textContent"))
                    .Should()
                    .Be("removed from project");

                await page.EvaluateAsync(Click(local, "Share with project"));
                await page.WaitAsync($"!!{Card(local)}.querySelector('form.aform')");
                (await ValueAsync(page, $"{Card(local)}.querySelector('form.aform').textContent"))
                    .Should()
                    .Contain("writes it to .harness/knowledge.toml now");
                await page.EvaluateAsync(Click(local, "Share"));
                await page.WaitAsync($"{Card(local)}?.querySelector('.shared')?.textContent === 'PROJECT'");
                memories.SharedStates(Token)[local].Should().Be(SharedState.Project);
                // the fixture's .gitignore hides the .harness folder: the author is told at once
                (await ValueAsync(page, "document.querySelector('.toast.bad')?.textContent"))
                    .Should()
                    .Contain("Git ignores the file");

                await page.EvaluateAsync(Click(local, "Withdraw from project"));
                await page.WaitAsync($"!!{Card(local)}.querySelector('form.aform')");
                (await ValueAsync(page, $"{Card(local)}.querySelector('form.aform').textContent"))
                    .Should()
                    .Contain("keeps it as your local memory");
                await page.EvaluateAsync(Click(local, "Withdraw"));
                await page.WaitAsync($"!{Card(local)}?.querySelector('.shared')");
                memories.SharedStates(Token).Should().NotContainKey(local);

                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + project + "/knowledge/inbox" }
                );
                await page.WaitAsync($"!!document.querySelector('[data-removed=\"{kept}\"]')");
                await page.EvaluateAsync(
                    $"[...document.querySelector('[data-removed=\"{kept}\"]').querySelectorAll('button')].find(b => b.textContent === 'Keep as local').click()"
                );
                await page.WaitAsync($"!document.querySelector('[data-removed=\"{kept}\"]')");
                await page.EvaluateAsync(
                    $"[...document.querySelector('[data-removed=\"{retired}\"]').querySelectorAll('button')].find(b => b.textContent === 'Retire').click()"
                );
                await page.WaitAsync($"!document.querySelector('[data-removed=\"{retired}\"]')");
                memories.SharedStates(Token).Should().BeEmpty("deciding clears both Inbox items");
                memories.Get(kept, Token).Current(new(DateTimeOffset.UtcNow)).Should().BeTrue();
                memories.Get(retired, Token).Current(new(DateTimeOffset.UtcNow)).Should().BeFalse();
            }
        );
    }

    /// <summary>
    /// Every page, at a desktop and a phone width, dark and light, with every fold closed and open, passes the Go
    /// harness's layout and accessibility checks (checks.js: overflow, overlapping or clipped text, squeezed columns,
    /// controls under 24 px, contrast under WCAG AA, sections showing an error) and raises no script error.
    /// </summary>
    [Fact]
    public async Task EveryPagePassesTheUiChecksInBothThemesAtBothWidths()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "example.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        fixture.Write(
            "Router.cs",
            "namespace Example;\npublic class Router\n{\n    public int Huge(int a)\n    {\n"
                + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", 29))
                + "        return a;\n    }\n}\n"
        );
        await fixture.CommitAsync("a project");
        await fixture.RunAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            var origin = new TaskOrigin("test", "test", "fixture");
            tasks.CreateBlueprint(new("M1", "First", "What it is for", ["it is done"]), "test", Token);
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0001",
                    Title = "A task in the blueprint",
                    Goal = "Its goal",
                    Blueprint = "M1",
                    Acceptance = ["checked"],
                },
                "",
                origin,
                Token
            );
            tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "A loose task" }, "", origin, Token);
            tasks.Propose(
                new TaskDefinition
                {
                    Title = "A proposed task",
                    Goal = "Found while working",
                    Acceptance = ["it works"],
                },
                "seen",
                "codex",
                Token
            );
            var memories = new MemoryStore(database);
            memories.Put(
                new()
                {
                    Type = "decision",
                    Content = "The router stays small",
                    Source = new("user", "test", "test"),
                },
                Token
            );
            // the team's knowledge: one shared, one a Git change took out of it (a Knowledge Inbox item)
            foreach (
                var (content, shared) in new[] { ("Routes are tested", "project"), ("Routes are cached", "removed") }
            )
            {
                var id = memories
                    .Put(
                        new()
                        {
                            Type = "convention",
                            Content = content,
                            Source = new("user", "test", "test"),
                        },
                        Token
                    )
                    .Memory!.Id;
                memories.SetShared(id, shared, Token);
            }
        }

        string checks;
        using (var stream = typeof(UiBrowserTests).Assembly.GetManifestResourceStream("checks.js")!)
        using (var reader = new StreamReader(stream))
        {
            checks = await reader.ReadToEndAsync(Token);
        }

        // each theme and width is a pass of its own, on its own page in its own browser context, run side by side:
        // a full page load holds the splash for over a second, and 104 of them one after another took minutes
        (string Theme, int Width)[] passes = [("dark", 1400), ("dark", 390), ("light", 1400), ("light", 390)];
        await BrowsePagesAsync(
            executable,
            fixture,
            passes.Length,
            async (tabs, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                foreach (var tab in tabs)
                {
                    await tab.CommandAsync("Page.enable", new { });
                    await tab.CommandAsync(
                        "Page.addScriptToEvaluateOnNewDocument",
                        new
                        {
                            source = """
                            window.__errors = [];
                            addEventListener('error', (e) => window.__errors.push(String(e.message || e.error)));
                            addEventListener('unhandledrejection', (e) => window.__errors.push('rejected: ' + String(e.reason && (e.reason.stack || e.reason))));
                            """,
                        }
                    );
                }

                var page = tabs[0];
                var root = server.Client.BaseAddress!.ToString().TrimEnd('/');
                await page.CommandAsync("Page.navigate", new { url = root + "/#/" });
                await page.WaitAsync("!!document.querySelector('#main h2, #main .card')");
                await page.EvaluateAsync(
                    $$"""
                    fetch('/api/workspaces', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Harness-UI': '1' },
                      body: JSON.stringify({ name: 'pages', project_ids: ['{{id}}'] }) })
                    """
                );
                await Task.Delay(500, Token);
                List<string> pages = ["", "search", "knowledge", "machine", "docs"];
                pages.AddRange(
                    new[]
                    {
                        "overview",
                        "architecture",
                        "architecture/layers",
                        "architecture/complexity",
                        "architecture/coupling",
                        "knowledge",
                        "knowledge/inbox",
                        "work",
                        "work/tasks",
                        "work/inbox",
                        "board",
                        "agents",
                        "agents/tools",
                        "health",
                    }.Select(view => $"p/{id}/{view}")
                );
                pages.AddRange(
                    new[]
                    {
                        "overview",
                        "architecture",
                        "architecture/build",
                        "architecture/libraries",
                        "knowledge",
                        "issues",
                        "issues/versions",
                        "members",
                    }.Select(view => "w/pages/" + view)
                );
                async Task<List<string>> PassAsync(BrowserPage tab, string theme, int width)
                {
                    List<string> found = [];
                    await tab.CommandAsync(
                        "Emulation.setDeviceMetricsOverride",
                        new
                        {
                            width,
                            height = 900,
                            deviceScaleFactor = 1,
                            mobile = width < 600,
                        }
                    );
                    await tab.CommandAsync(
                        "Emulation.setEmulatedMedia",
                        new { features = new[] { new { name = "prefers-color-scheme", value = theme } } }
                    );
                    foreach (var path in pages)
                    {
                        var at = $"{path} {width} {theme}";
                        await tab.CommandAsync("Page.navigate", new { url = "about:blank" });
                        await tab.CommandAsync("Page.navigate", new { url = root + "/#/" + path });
                        if (!await SettleAsync(tab))
                        {
                            found.Add(at + ": still loading after 20 s");
                        }
                        var closed = await ChecksAsync(tab, checks);
                        await tab.EvaluateAsync(
                            "document.querySelectorAll('details').forEach((d) => { d.open = true; })"
                        );
                        await SettleAsync(tab);
                        var open = await ChecksAsync(tab, checks);
                        found.AddRange(
                            closed
                                .Concat(open.Except(closed).Select(f => f + " (with every fold open)"))
                                .Select(f => $"{at}: {f}")
                        );
                        var errors = await ValueAsync(tab, "(window.__errors || ['no error log']).join(' | ')");
                        if (errors.Length > 0)
                        {
                            found.Add($"{at}: script error {errors}");
                        }
                    }

                    return found;
                }

                var faults = (
                    await Task.WhenAll(passes.Select((pass, index) => PassAsync(tabs[index], pass.Theme, pass.Width)))
                )
                    .SelectMany(found => found)
                    .ToList();
                faults.Should().BeEmpty(string.Join("\n", faults.Take(60)));

                // the checks do report: a control too small to hit
                await page.EvaluateAsync(
                    "document.body.append(Object.assign(document.createElement('button'), { textContent: 'x', style: 'width:10px;height:10px;padding:0' }))"
                );
                (await ChecksAsync(page, checks))
                    .Should()
                    .Contain(f => f.StartsWith("small-target", StringComparison.Ordinal));
            }
        );
    }

    /// <summary>The page has loaded, its sections drawn and its fonts in, twice in a row; false after 20 s.</summary>
    private static async Task<bool> SettleAsync(BrowserPage page)
    {
        await Task.Delay(400, Token);
        var still = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var ready = await ValueAsync(
                page,
                "document.readyState === 'complete' && !document.querySelector('.loadbar') && !document.getElementById('splash') && document.fonts.status === 'loaded'"
            );
            still = ready is "True" ? still + 1 : 0;
            if (still >= 2)
            {
                return true;
            }
            await Task.Delay(250, Token);
        }
        return false;
    }

    /// <summary>The checks' faults on the page as it is: rule, element, its text and the detail.</summary>
    private static async Task<IReadOnlyList<string>> ChecksAsync(BrowserPage page, string checks)
    {
        var json = await ValueAsync(page, "JSON.stringify(" + checks.Trim().TrimEnd(';') + ")");
        using var document = JsonDocument.Parse(json);
        return document
            .RootElement.EnumerateArray()
            .Select(f =>
                $"{f.GetProperty("rule").GetString()} {f.GetProperty("el").GetString()} \"{f.GetProperty("text").GetString()}\" {f.GetProperty("detail").GetString()}"
            )
            .ToArray();
    }

    [Fact]
    public async Task DependencySearchOffersTheWorkspacesOtherProjectsTasks()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var theirs = await Harness.Integration.Tasks.ExternalDependencyTests.TheirsAsync(fixture);
        var projects = fixture.Lifecycle.Registry.Load()["projects"]!.AsArray();
        var mine = UiWriteTests.ProjectId(fixture);
        var other = projects.Single(p => p!["name"]?.GetValue<string>() == "theirs")!["id"]!.GetValue<string>();
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                await page.CommandAsync("Page.navigate", new { url = server.Client.BaseAddress + "#/" });
                await page.WaitAsync("!!document.querySelector('#main h2, #main .card')");
                await page.EvaluateAsync(
                    $$"""
                    fetch('/api/workspaces', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Harness-UI': '1' },
                      body: JSON.stringify({ name: 'both', project_ids: ['{{mine}}', '{{other}}'] }) })
                    """
                );
                await Task.Delay(500, Token);
                await page.EvaluateAsync("location.hash = '#/p/" + mine + "/work/tasks'; location.reload()");
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'New task…')"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'New task…').click()"
                );
                await page.WaitAsync("!!document.querySelector('dialog[open] input[aria-label=\"Depends on\"]')");
                await page.EvaluateAsync(
                    "document.querySelector('dialog[open] input[aria-label=Title]').value = 'Waits for theirs'"
                );
                await page.EvaluateAsync(
                    "document.querySelector('dialog[open] input[aria-label=\"Depends on\"]').dispatchEvent(new FocusEvent('focus'))"
                );
                const string Theirs =
                    "[...document.querySelectorAll('dialog[open] .tpopt')].find(o => o.textContent.includes('OT-0001'))";
                await page.WaitAsync($"!!({Theirs})");
                (await ValueAsync(page, "document.querySelector('dialog[open] .tphead')?.textContent"))
                    .Should()
                    .Contain("other projects");
                await page.EvaluateAsync($"({Theirs}).click()");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('dialog[open] button')].find(b => b.textContent === 'Add the task').click()"
                );
                using var database = fixture.Database();
                var tasks = new TaskStore(database);
                await ProviderFixture.EventuallyAsync(
                    () => tasks.List(cancellationToken: Token).Any(t => t.Definition.Title == "Waits for theirs"),
                    TimeSpan.FromSeconds(15)
                );
                var task = tasks.List(cancellationToken: Token).Single(t => t.Definition.Title == "Waits for theirs");
                task.Definition.DependsOn.Should().Equal("theirs:OT-0001");
                task.Status.Should().Be("backlog", "their task is not done");
            }
        );
    }

    [Fact]
    public async Task EveryPageLoadsWithoutAScriptError()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "example.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        fixture.Write(
            "Router.cs",
            "namespace Example;\npublic class Router\n{\n    public int Route(int a) => a;\n}\n"
        );
        await fixture.CommitAsync("a project");
        await fixture.RunAsync();
        using (var database = fixture.Database())
        {
            var tasks = new TaskStore(database);
            tasks.CreateBlueprint(new("M1", "First"), "test", Token);
            tasks.Create(
                new TaskDefinition
                {
                    Id = "UI-0001",
                    Title = "A task",
                    Blueprint = "M1",
                },
                "",
                new("test", "test", "fixture"),
                Token
            );
        }

        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                // uncaught errors and rejected promises of every document, kept for the checks below
                await page.CommandAsync("Page.enable", new { });
                await page.CommandAsync(
                    "Page.addScriptToEvaluateOnNewDocument",
                    new
                    {
                        source = """
                        window.__errors = [];
                        addEventListener('error', (e) => window.__errors.push(String(e.message || e.error)));
                        addEventListener('unhandledrejection', (e) => window.__errors.push('rejected: ' + String(e.reason && (e.reason.stack || e.reason))));
                        """,
                    }
                );
                await page.CommandAsync("Page.navigate", new { url = server.Client.BaseAddress + "#/" });
                await page.WaitAsync("!!document.querySelector('#main h2, #main .card')");
                await page.EvaluateAsync(
                    $$"""
                    fetch('/api/workspaces', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Harness-UI': '1' },
                      body: JSON.stringify({ name: 'pages', project_ids: ['{{id}}'] }) })
                    """
                );
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                string[] routes =
                [
                    "#/",
                    $"#/p/{id}/overview",
                    $"#/p/{id}/architecture/structure",
                    $"#/p/{id}/architecture/layers",
                    $"#/p/{id}/architecture/complexity",
                    $"#/p/{id}/architecture/coupling",
                    $"#/p/{id}/knowledge",
                    $"#/p/{id}/knowledge/inbox",
                    $"#/p/{id}/work/blueprints",
                    $"#/p/{id}/work/tasks",
                    $"#/p/{id}/work/inbox",
                    $"#/p/{id}/work/UI-0001",
                    $"#/p/{id}/board",
                    $"#/p/{id}/agents/serving",
                    $"#/p/{id}/agents/tools",
                    $"#/p/{id}/health",
                    "#/w/pages/overview",
                    "#/w/pages/architecture/runtime",
                    "#/w/pages/architecture/build",
                    "#/w/pages/architecture/libraries",
                    "#/w/pages/knowledge",
                    "#/w/pages/issues",
                    "#/w/pages/members",
                    "#/knowledge",
                    "#/machine",
                    "#/docs",
                    "#/search?q=task",
                ];
                foreach (var route in routes)
                {
                    await page.EvaluateAsync($"location.hash = '{route}'");
                    await page.WaitAsync(
                        "!!document.querySelector('#main') && document.querySelector('#main').children.length > 0"
                            + " && !document.querySelector('#main .loadbar')"
                    );
                    (await ValueAsync(page, "(window.__errors || ['no error log']).join(' | ')"))
                        .Should()
                        .BeEmpty(route);
                }
            }
        );
    }

    /// <summary>Forces CSS pseudo-classes (hover, focus) on the element a selector finds, or clears them.</summary>
    private static async Task ForcePseudoStateAsync(BrowserPage page, string selector, string[] states)
    {
        await page.CommandAsync("DOM.enable", new { });
        await page.CommandAsync("CSS.enable", new { });
        var root = (await page.CommandAsync("DOM.getDocument", new { depth = 0 }))
            .GetProperty("root")
            .GetProperty("nodeId")
            .GetInt32();
        var node = (await page.CommandAsync("DOM.querySelector", new { nodeId = root, selector }))
            .GetProperty("nodeId")
            .GetInt32();
        await page.CommandAsync("CSS.forcePseudoState", new { nodeId = node, forcedPseudoClasses = states });
    }

    /// <summary>"ok" when a button's text has WCAG AA contrast (4.5:1) on what is behind it, else the ratio.</summary>
    private static string ButtonContrast(string selector) =>
        $$"""
            (() => {
              const cv = document.createElement('canvas'); cv.width = cv.height = 1;
              const g = cv.getContext('2d', { willReadFrequently: true });
              const px = (...cs) => { g.clearRect(0, 0, 1, 1); for (const c of cs) { g.fillStyle = c; g.fillRect(0, 0, 1, 1); } return [...g.getImageData(0, 0, 1, 1).data].slice(0, 3); };
              const lum = (rgb) => { const [r, gr, b] = rgb.map((v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * gr + 0.0722 * b; };
              const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
              const b = document.querySelector('{{selector}}');
              const layers = []; for (let e = b; e; e = e.parentElement) layers.unshift(getComputedStyle(e).backgroundColor);
              const back = px(...layers);
              const r = ratio(px(...layers, getComputedStyle(b).color), back);
              return r >= 4.5 ? 'ok' : r.toFixed(2) + ' ' + getComputedStyle(b).color + ' on ' + getComputedStyle(b).backgroundColor;
            })()
            """;

    private static async Task<string> ValueAsync(BrowserPage page, string expression) =>
        (await page.EvaluateAsync(expression)).GetProperty("result").GetProperty("value").ToString();

    private static string Browser()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_UI_BROWSER");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_UI_BROWSER to a Chrome or Edge executable for headless browser review checks."
            );
        }

        // a 32-bit browser on 64-bit Windows starves the machine: the 64-bit msedge.exe is the one to use
        return !OperatingSystem.IsWindows() || Is64Bit(executable)
            ? executable
            : throw Xunit.Sdk.SkipException.ForSkip(
                $"HARNESS_UI_BROWSER points at a 32-bit browser ({executable}): use the 64-bit msedge.exe "
                    + "(installed as C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe)."
            );
    }

    /// <summary>Whether a Windows executable is built for x64 or Arm64, read from its PE header.</summary>
    private static bool Is64Bit(string executable)
    {
        using var file = File.OpenRead(executable);
        using var reader = new BinaryReader(file);
        file.Position = 0x3C;
        file.Position = reader.ReadInt32() + 4;
        return reader.ReadUInt16() is 0x8664 or 0xAA64;
    }

    /// <summary>
    /// Serves the fixture's UI and runs the checks on a page of the shared browser, in a browser context of its own
    /// that is disposed when the checks end. A test that names a profile gets a browser of its own instead, started
    /// on that profile in a kill-on-close job (a process group elsewhere) and ended with the test, with every helper
    /// process it started.
    /// </summary>
    private async Task BrowseAsync(
        string executable,
        SyncFixture fixture,
        Func<BrowserPage, UiFixture, Task> checks,
        string? profile = null
    )
    {
        if (profile is null)
        {
            await BrowsePagesAsync(executable, fixture, 1, (pages, server) => checks(pages[0], server));
            return;
        }

        await using var server = await UiFixture.StartAsync(fixture);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler, disposeHandler: false);
        await using var edge = await SharedBrowser.Edge.StartAsync(executable, profile, client, Token);
        using var socket = await edge.FirstPageAsync(client, Token);
        await checks(new BrowserPage(socket), server);
    }

    /// <summary>
    /// Serves the fixture's UI and runs the checks on several pages of the shared browser at once, each in a browser
    /// context of its own, so they share nothing but the server.
    /// </summary>
    private async Task BrowsePagesAsync(
        string executable,
        SyncFixture fixture,
        int count,
        Func<IReadOnlyList<BrowserPage>, UiFixture, Task> checks
    )
    {
        await using var server = await UiFixture.StartAsync(fixture);
        List<SharedBrowser.IsolatedPage> pages = [];
        try
        {
            for (var index = 0; index < count; index++)
            {
                pages.Add(await browser.OpenAsync(executable, Token));
            }

            await checks(pages.Select(page => page.Page).ToArray(), server);
        }
        finally
        {
            foreach (var page in pages)
            {
                await page.DisposeAsync();
            }
        }
    }

    /// <summary>The ids of the browser processes (the browser and its helpers) that use this profile directory.</summary>
    private static async Task<IReadOnlyList<int>> BrowserProcessesAsync(string profile)
    {
        if (OperatingSystem.IsWindows())
        {
            // read each process's command line directly: a WMI query (Get-CimInstance) takes some 10 s
            List<int> ids = [];
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (
                        process.Id != Environment.ProcessId
                        && CommandLines.Of(process.Id)?.Contains(profile, StringComparison.Ordinal) is true
                    )
                    {
                        ids.Add(process.Id);
                    }
                }
            }

            return ids;
        }

        using var ps = new Process
        {
            StartInfo = new("ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                ArgumentList = { "-eo", "pid=,args=" },
            },
        };
        ps.Start();
        var output = await ps.StandardOutput.ReadToEndAsync(Token);
        await ps.WaitForExitAsync(Token);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains(profile, StringComparison.Ordinal))
            .Select(line => int.Parse(line.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .Where(id => id != Environment.ProcessId)
            .ToArray();
    }

    /// <summary>A Windows process's command line, read with NtQueryInformationProcess (Windows 8.1 and later).</summary>
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute to generate P/Invoke marshalling code at compile time",
        Justification = "The repository disallows unsafe code; these bounded Windows ABI declarations use runtime marshalling."
    )]
    private static class CommandLines
    {
        private const int QueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        /// <summary>The command line, or null for a process that is gone or may not be queried.</summary>
        internal static string? Of(int id)
        {
            using var handle = OpenProcess(QueryLimitedInformation, false, id);
            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new byte[8192];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var status = NtQueryInformationProcess(
                    handle,
                    ProcessCommandLineInformation,
                    buffer,
                    buffer.Length,
                    out var needed
                );
                if (status is 0)
                {
                    // a UNICODE_STRING (length, maximum, pointer) whose characters follow it in the buffer
                    var length = BitConverter.ToUInt16(buffer, 0);
                    return System.Text.Encoding.Unicode.GetString(buffer, 2 * IntPtr.Size, length);
                }

                if (needed <= buffer.Length)
                {
                    return null;
                }

                buffer = new byte[needed];
            }

            return null;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int id);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            SafeProcessHandle process,
            int informationClass,
            byte[] information,
            int length,
            out int returned
        );
    }
}

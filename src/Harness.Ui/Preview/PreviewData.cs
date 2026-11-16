using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Preview;

/// <summary>Sample data for the IDE designer. It is never assigned as a runtime data context.</summary>
public static class PreviewData
{
    private static readonly Uri Address = new("https://preview.invalid/");
    private static readonly HttpClient Client = new(new PreviewApiHandler()) { BaseAddress = Address };

    // Every preview has its own preferences, dialogs and live-update registrations.
    private static UiServices Services => new(
        new HarnessApi(Client),
        new UiHostContext
        {
            Server = Address,
            Navigation = new InMemoryNavigation(),
            Preferences = new InMemoryPreferences(),
        }
    );

    private static ProjectInfo Project => new()
    {
        Id = "prj_fixture",
        Title = "Fixture project",
        Root = "/home/example/fixture",
        Live = new ProjectLive
        {
            State = "ok", PendingProposals = 1, PendingArchProposals = 0, PendingTaskProposals = 1,
            PendingBlueprintProposals = 0, RemovedFromProject = 0, ChecksFailed = 0, ChecksWarned = 0,
            Branch = "main", Commit = "1234567",
        },
    };

    private static WorkspaceInfo Workspace => new()
    {
        Id = "ws_fixture", Name = "example", Title = "Example workspace",
        Members =
        [
            new WorkspaceMember { ProjectId = "prj_fixture", Project = "fixture" },
            new WorkspaceMember { ProjectId = "prj_api", Project = "api" },
            new WorkspaceMember { ProjectId = "prj_worker", Project = "worker" },
            new WorkspaceMember { ProjectId = "prj_core", Project = "core" },
        ],
    };

    private static ProjectInfo Member(string id, string name) => new()
    {
        Id = id, Name = name, Title = name, Root = "/home/example/" + name, Live = Project.Live,
    };

    private static IReadOnlyList<ProjectInfo> Projects =>
        [Project, Member("prj_api", "Api"), Member("prj_worker", "Worker"), Member("prj_core", "Core")];

    private static WorkspaceContext WorkspaceContext => new(Services, Workspace, Projects);

    public static MainViewModel Main
    {
        get
        {
            var model = new MainViewModel(Services);
            model.InitializePreview(Projects, Workspace);
            return model;
        }
    }

    public static ProjectPage ProjectPage => new(Services, Project, Route.Parse("#/p/prj_fixture/overview"));

    public static WorkspacePage WorkspacePage =>
        new(Services, Workspace, Projects, Route.Parse("#/w/example/overview"));

    public static OverviewViewModel Overview
    {
        get
        {
            var model = new OverviewViewModel(Services, Project);
            model.Start();
            return model;
        }
    }

    public static ArchitectureViewModel Architecture
    {
        get
        {
            var model = new ArchitectureViewModel(Services, Project.Id, Route.Parse("#/p/prj_fixture/architecture"));
            model.Start();
            return model;
        }
    }

    public static BoardViewModel Board
    {
        get
        {
            var model = new BoardViewModel(Services, Project.Id, id => Route.ProjectHref(Project.Id, "work", id));
            _ = model.LoadAsync();
            return model;
        }
    }

    public static WorkViewModel Work
    {
        get
        {
            var model = new WorkViewModel(Services, Project, Route.Parse("#/p/prj_fixture/work"));
            model.Start();
            return model;
        }
    }

    public static KnowledgeViewModel Knowledge
    {
        get
        {
            var model = new KnowledgeViewModel(Services, Project, Route.Parse("#/p/prj_fixture/knowledge"));
            model.Start();
            return model;
        }
    }

    public static AgentsViewModel Agents
    {
        get
        {
            var model = new AgentsViewModel(Services, Project.Id, Route.Parse("#/p/prj_fixture/agents"));
            model.Start();
            return model;
        }
    }

    public static HealthViewModel Health
    {
        get
        {
            var model = new HealthViewModel(Services, Project.Id);
            _ = model.LoadAsync();
            return model;
        }
    }

    public static MachinePage Machine => new(Services);
    public static DocsPage Docs => new(Services, Route.Parse("#/docs/guide-what-harness-is-for"));
    public static SearchPage Search => new(Services, Route.Parse("#/search?q=fixture"));
    public static WorkspaceMembersViewModel WorkspaceMembers => new(WorkspaceContext);
    public static WorkspaceArchitectureViewModel WorkspaceArchitecture =>
        new(WorkspaceContext, Route.Parse("#/w/example/architecture"));
    public static WorkspaceIssuesViewModel WorkspaceIssues =>
        new(WorkspaceContext, Route.Parse("#/w/example/issues"));
    public static WorkspaceOverviewViewModel WorkspaceOverview
    {
        get
        {
            var model = new WorkspaceOverviewViewModel(WorkspaceContext);
            model.Start();
            return model;
        }
    }

    public static TaskCardViewModel TaskCard => new(
        new BoardItem(
            "TASK-0001", "ready", 0, "Add a useful preview", "high", "feature", "BP-010", "UI parity", "teal"
        ),
        "#/p/prj_fixture/work/TASK-0001"
    );

    private static Memory Memory => new()
    {
        Id = "mem_preview", Type = "decision", Content = "Keep previews independent of live project state.",
        Scope = "project", SourceType = "user", CreatedAt = "2026-01-01T00:00:00Z", SourceRef = "Example",
    };

    public static MemoryCard MemoryCard => new(Memory, new KnowledgeActions(Services, Project.Id, []), false);
    public static MemoryGroup MemoryGroup => new("decision", [MemoryCard]);

    public static LevelKnowledgeViewModel LevelKnowledge
    {
        get
        {
            var model = new LevelKnowledgeViewModel(Services, "user", "Knowledge shared across projects.");
            _ = model.LoadAsync();
            return model;
        }
    }

    public static FindingsBoard Findings
    {
        get
        {
            var board = WorkspaceContext.Board();
            board.Show(
                [new Finding
                {
                    Kind = "complexity", Level = "warn", Title = "Example has many paths", Why = "Consider splitting it.",
                }],
                null,
                false
            );
            return board;
        }
    }

    public static SectionViewModel Section => Health;
    public static TaskDialog TaskDialog => new(Services, Project.Id, "TASK-0001") { Title = "TASK-0001 · Ready one" };
    public static RejectDialog Reject => new(Services, Project.Id, "task", "example") { Title = "Reject proposal" };
    public static ArchApproveDialog ArchApprove => new(Services, Project.Id, new ArchProposal
    {
        Id = "arch-preview", Status = "pending", ProposedBy = "agent", ProposedAt = "2026-01-01T00:00:00Z",
        Kind = "task", Scope = "project", Basis = "observation", Title = "Simplify the boundary",
        Body = "Review the dependency."
    }) { Title = "Review proposal" };

    private static UiServices DialogServices() => Services;

    public static TaskFormDialog TaskForm
    {
        get
        {
            var services = DialogServices();
            _ = TaskFormDialog.EditAsync(services, Project.Id, [], [], new TaskSummary
            {
                Id = "TASK-0001", Title = "Add a useful preview", Status = "ready", Goal = "Show representative data.",
                Acceptance =
                [
                    new AcceptanceCriterion
                    {
                        Index = 1, Criterion = "The preview shows sample content", Result = "pending",
                    },
                ],
                Scope = ["src/Harness.Ui"],
            });
            return (TaskFormDialog)services.Dialogs.Current!;
        }
    }

    public static BlueprintFormDialog BlueprintForm
    {
        get
        {
            var services = DialogServices();
            _ = BlueprintFormDialog.EditAsync(services, Project.Id, new BlueprintInfo
            {
                Id = "BP-010", Title = "UI parity", Goal = "Make the UI clear and accessible.",
                Exit = ["The key views can be previewed."], Color = "teal", Defined = true,
            });
            return (BlueprintFormDialog)services.Dialogs.Current!;
        }
    }

    public static RemoveBlueprintDialog RemoveBlueprint
    {
        get
        {
#pragma warning disable CA2000 // The returned dialog retains its preview services for its lifetime.
            var services = DialogServices();
#pragma warning restore CA2000
            _ = RemoveBlueprintDialog.AskAsync(services, Project.Id,
                new BlueprintInfo { Id = "BP-010", Title = "UI parity", Total = 3 }, []);
            return (RemoveBlueprintDialog)services.Dialogs.Current!;
        }
    }

    public static ToastService Toasts
    {
        get
        {
            var service = new ToastService();
            service.Items.Add(new Toast("Changes saved", null, false));
            service.Items.Add(new Toast("Example warning", "A longer detail for the preview.", true));
            return service;
        }
    }

    public static DialogService Dialog
    {
        get
        {
            var service = new DialogService();
            _ = service.ShowAsync(new ConfirmDialog
            {
                Title = "Confirm change", Message = "Review the change before continuing.",
            });
            return service;
        }
    }

    public static HomePage Home
    {
        get
        {
            var page = new HomePage();
            page.Update(Projects, [Workspace], Route.Home);
            return page;
        }
    }
}

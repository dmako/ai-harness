using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The building blocks every view uses (TASK-0148): sections loading on their own, dialogs and forms, toasts, live
/// updates by project and kind, the write path, and the link affordances.
/// </summary>
public sealed class SharedBehaviourTests
{
    [Fact]
    public Task ASlowOrFailingSectionNeverHoldsUpTheOthers() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/fast"] = """{"text":"fast answer"}""";
        api.Answers["/api/slow"] = """{"text":"slow answer"}""";
        api.Delays["/api/slow"] = TimeSpan.FromSeconds(2);
        api.Failures["/api/broken"] = HttpStatusCode.InternalServerError;
        using var http = new HttpClient(api) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http);
        TestSection[] sections =
        [
            new(services, "Fast", "/api/fast"),
            new(services, "Slow", "/api/slow"),
            new(services, "Broken", "/api/broken"),
        ];
        var window = Page(sections);
        var loads = sections.Select(s => s.LoadAsync()).ToArray();

        await Ui.UntilAsync(() => sections[0].HasContent && sections[2].HasError);
        sections[0].Text.Should().Be("fast answer");
        sections[1].Loading.Should().BeTrue("the slow one is still loading while the others are shown");
        sections[2].Error.Should().Contain("500");
        var views = window.GetVisualDescendants().OfType<SectionView>().ToArray();
        views[1].FindControl<StackPanel>("Loading")!.IsVisible.Should().BeTrue();
        views[2].FindControl<Border>("Failure")!.IsVisible.Should().BeTrue();

        api.Failures.Clear();
        api.Answers["/api/broken"] = """{"text":"fixed"}""";
        await sections[2].RetryCommand.ExecuteAsync(null);
        sections[2].Text.Should().Be("fixed");
        sections[2].HasError.Should().BeFalse();
        await Task.WhenAll(loads);
        sections[1].Text.Should().Be("slow answer");
        window.Close();
    });

    [Fact]
    public Task EscCancelsAndEnterAcceptsAConfirmation() => Ui.RunAsync(async () =>
    {
        using var http = new HttpClient(new FixtureApi()) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http);
        var window = Ui.Show(new DialogLayer { DataContext = services.Dialogs }, 800, 600);

        var first = services.Dialogs.ConfirmAsync(new ConfirmDialog { Title = "Delete TASK-0001?", Danger = true, AcceptLabel = "Delete" });
        await Ui.UntilAsync(() => window.FocusManager.GetFocusedElement() is Button { Name: "Accept" });
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        (await first).Should().BeFalse();
        services.Dialogs.IsOpen.Should().BeFalse();

        var second = services.Dialogs.ConfirmAsync(new ConfirmDialog { Title = "Approve?" });
        await Ui.UntilAsync(() => services.Dialogs.IsOpen);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        (await second).Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task AFormFocusesItsFirstFieldChecksItShowsTheReviewAndKeepsARefusal() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Failures["/api/rename"] = HttpStatusCode.BadRequest;
        using var http = new HttpClient(api) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http);
        var layer = new DialogLayer { DataContext = services.Dialogs };
        layer.DataTemplates.Add(
            new FuncDataTemplate<RenameForm>(
                (_, _) => new TextBox { Name = "NameField", [!TextBox.TextProperty] = new Binding(nameof(RenameForm.Name)) }
            )
        );
        var window = Ui.Show(layer, 800, 600);
        var form = new RenameForm(services) { Title = "Rename", AcceptLabel = "Rename" };

        var result = services.Dialogs.ShowAsync(form);
        await Ui.UntilAsync(() => window.FocusManager.GetFocusedElement() is TextBox { Name: "NameField" });

        await form.AcceptCommand.ExecuteAsync(null);
        form.Error.Should().Be("Give a name.");
        form.Name = "harness";
        await form.AcceptCommand.ExecuteAsync(null);
        form.Reviewing.Should().BeTrue();
        form.ReviewText.Should().Be("The project will be named harness.");
        api.Posts.Should().BeEmpty("the review comes before anything is sent");

        await form.AcceptCommand.ExecuteAsync(null);
        form.Error.Should().Contain("400");
        services.Dialogs.IsOpen.Should().BeTrue("a refused write keeps the form open");

        api.Failures.Clear();
        await form.AcceptCommand.ExecuteAsync(null);
        (await result).Should().Be("harness");
        api.Posts.Should().HaveCount(2, "the refused write and the one that went through");
        window.Close();
    });

    [Fact]
    public Task ASuccessToastGoesQuicklyAFailureStaysWithItsDetail() => Ui.RunAsync(async () =>
    {
        var toasts = new ToastService(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800));
        var window = Ui.Show(new ToastLayer { DataContext = toasts }, 800, 600);
        toasts.Show("Done");
        toasts.Fail("Not done", "403: writes need the X-Harness-UI: 1 header");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("403: writes need the X-Harness-UI: 1 header");

        await Task.Delay(400);
        await Ui.UntilAsync(() => toasts.Items.Count is 1);
        toasts.Items.Single().Bad.Should().BeTrue();
        await Ui.UntilAsync(() => toasts.Items.Count is 0);
        window.Close();
    });

    [Fact]
    public Task LiveChangesAreCoalescedSpacedAndReachOnlyTheSectionsTheyConcern() => Ui.RunAsync(async () =>
    {
        using var http = new HttpClient(new FixtureApi()) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http, fastLive: true);
        var tasks = new TestSection(services, "p1 tasks", null, "p1", ["tasks"]);
        var knowledge = new TestSection(services, "p1 knowledge", null, "p1", ["knowledge"]);
        var other = new TestSection(services, "p2 tasks", null, "p2", ["tasks"]);
        foreach (var s in new[] { tasks, knowledge, other })
        {
            s.Follow(services.Live);
        }

        for (var i = 0; i < 5; i++)
        {
            services.Live.Changed(Change("p1", "tasks"));
            await Task.Delay(20);
        }
        await Ui.UntilAsync(() => services.Live.Applies is 1);
        tasks.Loads.Should().Be(1, "five changes in a burst are one reload");
        knowledge.Loads.Should().Be(0);
        other.Loads.Should().Be(0);

        var applied = DateTime.UtcNow;
        services.Live.Changed(Change("p1", "knowledge"));
        await Ui.UntilAsync(() => services.Live.Applies is 2);
        (DateTime.UtcNow - applied).Should().BeGreaterThan(TimeSpan.FromMilliseconds(400), "applies keep their gap");
        knowledge.Loads.Should().Be(1);
        tasks.Loads.Should().Be(1);

        services.Live.Changed(new LiveChange { Projects = ["p2"] });
        await Ui.UntilAsync(() => services.Live.Applies is 3);
        other.Loads.Should().Be(1, "a project changed without kinds is all of its kinds");
    });

    [Fact]
    public Task NothingAppliesWhileADialogIsOpen() => Ui.RunAsync(async () =>
    {
        using var http = new HttpClient(new FixtureApi()) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http, fastLive: true);
        var section = new TestSection(services, "p1", null, "p1", ["tasks"]);
        section.Follow(services.Live);
        var dialog = new ConfirmDialog { Title = "Open" };
        var open = services.Dialogs.ConfirmAsync(dialog);

        services.Live.Changed(Change("p1", "tasks"));
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();
        section.Loads.Should().Be(0, "the form stays as it is while it is open");

        dialog.Cancel();
        await open;
        await Ui.UntilAsync(() => section.Loads is 1);
    });

    [Fact]
    public Task ADroppedStreamReconnectsAndRefreshesEverything() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        using var http = new HttpClient(api) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http, fastLive: true);
        var section = new TestSection(services, "unrelated", null, "p9", ["tasks"]);
        section.Follow(services.Live);
        using var stop = new CancellationTokenSource();
        var follow = services.Live.FollowAsync(services.Api, stop.Token);
        await Ui.UntilAsync(() => services.Live.State is "live");

        api.Drop();
        await Ui.UntilAsync(() => api.Streams is 2 && services.Live.State is "live");
        await Ui.UntilAsync(() => section.Loads is 1);
        section.Loads.Should().Be(1, "after a reconnect everything is refreshed, even sections no change named");
        await stop.CancelAsync();
        await follow;
    });

    [Fact]
    public Task AWriteSendsTheHeadersSaysDoneAndReloadsEverySection() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        using var http = new HttpClient(api) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http, fastLive: true);
        var section = new TestSection(services, "p1", null, "p1", ["tasks"]);
        section.Follow(services.Live);

        await services.WriteAsync("/api/projects/p1/rename", new Dictionary<string, string> { ["title"] = "T" }, TestJson.Default.DictionaryStringString, CancellationToken.None);

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Ui.Should().Be("1");
        post.Origin.Should().Be("http://127.0.0.1:17477");
        post.Body.Should().Be("""{"title":"T"}""");
        services.Toasts.Items.Should().ContainSingle(t => t.Text == "Done" && !t.Bad);
        await Ui.UntilAsync(() => section.Loads is 1);

        api.Failures["/api/projects/p1/rename"] = HttpStatusCode.Forbidden;
        await FluentActions
            .Awaiting(() => services.WriteAsync("/api/projects/p1/rename", new Dictionary<string, string>(), TestJson.Default.DictionaryStringString, CancellationToken.None))
            .Should()
            .ThrowAsync<HarnessApiException>();
        services.Toasts.Items.Should().Contain(t => t.Bad && t.Detail!.Contains("403", StringComparison.Ordinal));
    });

    [Fact]
    public Task LinksOfferOpenInNewTabAndCopyAndMiddleClickOpens() => Ui.RunAsync(async () =>
    {
        var opened = new List<Uri>();
        var previous = Links.Opener;
        Links.Opener = (_, uri) =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };
        try
        {
            var link = new HyperlinkButton { Content = "TASK-0001", NavigateUri = new Uri("http://127.0.0.1:17477/#/p/x/work/tasks/TASK-0001") };
            var chip = new Button { Classes = { "copychip" }, Content = "workspace:main" };
            Links.SetCopy(chip, "workspace:main");
            var window = Ui.Show(new StackPanel { Children = { link, chip } }, 400, 200);

            var items = link.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            items.Select(i => (string?)i.Header).Should().Equal("Open in new tab", "Copy link");
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Ui.UntilAsync(() => opened.Count is 1);
            (await window.Clipboard!.TryGetTextAsync()).Should().Be(link.NavigateUri.ToString());

            var center = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Middle);
            window.MouseUp(center, MouseButton.Middle);
            await Ui.UntilAsync(() => opened.Count is 2);

            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Ui.UntilAsync(() => chip.Classes.Contains("copied"));
            (await window.Clipboard!.TryGetTextAsync()).Should().Be("workspace:main");
            window.Close();
        }
        finally
        {
            Links.Opener = previous;
        }
    });

    private static LiveChange Change(string project, string kind) =>
        new()
        {
            Projects = [project],
            Kinds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [project] = [kind] },
        };

    private static Window Page(IEnumerable<SectionViewModel> sections)
    {
        var panel = new StackPanel();
        foreach (var section in sections)
        {
            panel.Children.Add(new SectionView { DataContext = section });
        }
        var window = Ui.Show(panel, 800, 600);
        window.DataTemplates.Add(
            new FuncDataTemplate<TestSection>((_, _) => new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(TestSection.Text)) })
        );
        return window;
    }

    /// <summary>A section showing one field of a JSON answer, counting its loads.</summary>
    private sealed partial class TestSection(UiServices services, string title, string? path, string? project, IReadOnlyList<string>? kinds)
        : SectionViewModel(title, project, kinds)
    {
        public TestSection(UiServices services, string title, string path)
            : this(services, title, path, null, null) { }

        public string? Text { get; private set; }
        public int Loads { get; private set; }

        protected override async Task LoadCoreAsync(CancellationToken token)
        {
            Loads++;
            if (path is not null)
            {
                Text = (await services.Api.ReadAsync(path, token)).GetProperty("text").GetString();
                OnPropertyChanged(nameof(Text));
            }
        }
    }

    /// <summary>A form with one field, a review, and a write.</summary>
    private sealed class RenameForm(UiServices services) : FormDialog<string>
    {
        public string Name { get; set; } = "";

        protected override string? Validate() => Name.Length is 0 ? "Give a name." : null;

        protected override string? Review() => $"The project will be named {Name}.";

        protected override async Task<string> SendAsync(CancellationToken token)
        {
            await services.Api.PostAsync("/api/rename", new Dictionary<string, string> { ["name"] = Name }, TestJson.Default.DictionaryStringString, token);
            return Name;
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TestJson : JsonSerializerContext;

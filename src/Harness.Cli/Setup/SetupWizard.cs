namespace Harness.Cli.Setup;

internal sealed class SetupItem(string label, string where, bool installed, Func<bool, Task<int>> apply)
{
    public string Label { get; } = label;
    public string Where { get; } = where;
    public bool Installed { get; } = installed;
    public bool Want { get; set; } = installed;
    public int Change => (Want, Installed) switch { (true, false) => 1, (false, true) => -1, _ => 0 };
    public Func<bool, Task<int>> Apply { get; } = apply;
}
internal sealed record SetupStep(string Title, string Note, IReadOnlyList<SetupItem> Items);
internal enum WizardResult { Continue, Apply, Cancel }
internal sealed class SetupWizard(string title, IReadOnlyList<SetupStep> steps)
{
    public string Title { get; } = title;
    public IReadOnlyList<SetupStep> Steps { get; } = steps;
    public int Step { get; private set; }
    public int Cursor { get; private set; }
    public bool Reviewing => Step == Steps.Count;
    public IEnumerable<SetupItem> Changes => Steps.SelectMany(s => s.Items).Where(i => i.Change is not 0);
    public WizardResult Handle(string key)
    {
        if (key is "q" or "escape" or "cancel")
            return WizardResult.Cancel;
        if (key is "b" or "left" or "backspace")
        {
            Step = Math.Max(0, Step - 1);
            Cursor = 0;
            return WizardResult.Continue;
        }
        if (Reviewing)
            return key is "enter" or "y" ? WizardResult.Apply : WizardResult.Continue;
        var items = Steps[Step].Items;
        switch (key)
        {
            case "up" or "k":
                Cursor = Math.Max(0, Cursor - 1);
                break;
            case "down" or "j":
                Cursor = Math.Max(0, Math.Min(items.Count - 1, Cursor + 1));
                break;
            case " " or "x":
                if (items.Count > 0)
                    items[Cursor].Want = !items[Cursor].Want;
                break;
            case "enter" or "right":
                Step++;
                Cursor = 0;
                break;
        }
        return WizardResult.Continue;
    }
    public async Task<int> ApplyAsync(TextWriter output)
    {
        var changes = Changes.ToArray();
        if (changes.Length is 0)
        { await output.WriteLineAsync("Nothing to change."); return 0; }
        var code = 0;
        foreach (var item in changes.OrderBy(i => i.Change))
        {
            await output.WriteLineAsync((item.Change < 0 ? "Removing " : "Installing ") + item.Label);
            var result = await item.Apply(item.Change > 0);
            if (result is not 0)
                code = result;
        }
        return code;
    }
    public async Task RenderAsync(TextWriter output, int width)
    {
        async Task Line(string text) => await output.WriteLineAsync(text.Length > width ? text[..Math.Max(0, width - 1)] + "…" : text);
        var name = Reviewing ? "Review" : Steps[Step].Title;
        await Line($"{Title} · step {Step + 1}/{Steps.Count + 1} · {name}");
        if (Reviewing)
        {
            var changes = Changes.ToArray();
            if (changes.Length is 0)
                await Line("  Nothing to change.");
            foreach (var item in changes)
                await Line($"  {(item.Change > 0 ? "+ install" : "- remove ")}  {item.Label,-28} {item.Where}");
            await Line("enter apply · b back · q cancel");
            return;
        }
        var step = Steps[Step];
        await Line("  " + step.Note);
        if (step.Items.Count is 0)
            await Line("  (nothing to set up here)");
        for (var i = 0; i < step.Items.Count; i++)
        {
            var item = step.Items[i];
            var state = item.Installed ? "installed" : "not installed";
            if (item.Change is not 0)
                state += item.Change > 0 ? " → install" : " → remove";
            await Line($"{(i == Cursor ? "> " : "  ")}{(item.Want ? "[x]" : "[ ]")} {item.Label,-28} {state,-26} {item.Where}");
        }
        await Line("↑/↓ move · space toggle · enter next · b back · q cancel");
    }
    public async Task StatusAsync(TextWriter output)
    {
        foreach (var step in Steps)
        {
            await output.WriteLineAsync(step.Title + ":");
            foreach (var item in step.Items)
                await output.WriteLineAsync($"  {item.Label,-28} {(item.Installed ? "installed" : "not installed"),-16} {item.Where}");
        }
        await output.WriteLineAsync("\nRun it in a terminal to choose, or use the flags (" + Title + " --help).");
    }
}
internal class SetupTerminal
{
    protected virtual bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    protected virtual string ReadKey() => Console.ReadKey(intercept: true) switch
    {
        { Key: ConsoleKey.Enter } => "enter",
        { Key: ConsoleKey.Escape } => "escape",
        { Key: ConsoleKey.UpArrow } => "up",
        { Key: ConsoleKey.DownArrow } => "down",
        { Key: ConsoleKey.LeftArrow } => "left",
        { Key: ConsoleKey.RightArrow } => "right",
        { Key: ConsoleKey.Backspace } => "backspace",
        { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control } => "cancel",
        var key => key.KeyChar.ToString(),
    };
    public async Task<int> RunAsync(SetupWizard wizard, TextWriter output, CancellationToken token)
    {
        if (!Interactive)
        { await wizard.StatusAsync(output); return 0; }
        var result = WizardResult.Continue;
        var controlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        await output.WriteAsync("\x1b[?1049h\x1b[?25l");
        try
        {
            while (result is WizardResult.Continue)
            {
                token.ThrowIfCancellationRequested();
                await output.WriteAsync("\x1b[H\x1b[2J");
                await wizard.RenderAsync(output, Console.WindowWidth);
                while (!Console.KeyAvailable)
                    await Task.Delay(25, token);
                result = wizard.Handle(ReadKey());
            }
        }
        finally { Console.TreatControlCAsInput = controlC; await output.WriteAsync("\x1b[?25h\x1b[?1049l"); }
        if (result is WizardResult.Cancel)
        { await output.WriteLineAsync("Cancelled: nothing changed."); return 0; }
        return await wizard.ApplyAsync(output);
    }
}

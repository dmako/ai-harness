using System.CommandLine;
using System.CommandLine.Parsing;

namespace Harness.Cli.Scm;

/// <summary>Typed options and arguments of 'harness scm', bound without reflection like <see cref="CommandBindings" />.</summary>
internal static class ScmBindings
{
    internal static Func<ParseResult, ScmSettings> BindList(Command command)
    {
        var json = Json(command);
        return result => new ScmSettings { Json = result.GetValue(json) };
    }

    internal static Func<ParseResult, ScmNameSettings> BindTest(Command command)
    {
        var json = Json(command);
        var name = new Argument<string>("name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "",
            Description = "The SCM to test; all of them when left out",
        };
        command.Arguments.Add(name);
        return result => new ScmNameSettings { Json = result.GetValue(json), Name = result.GetValue(name) ?? "" };
    }

    internal static Func<ParseResult, ScmNameSettings> BindRemove(Command command)
    {
        var json = Json(command);
        var name = Name(command);
        return result => new ScmNameSettings { Json = result.GetValue(json), Name = result.GetValue(name) ?? "" };
    }

    internal static Func<ParseResult, ScmTokenSettings> BindAdd(Command command)
    {
        var json = Json(command);
        var name = Name(command);
        var (type, url, token, tokenStdin, tokenEnv) = TokenOptions(command, required: true);
        return result => new ScmTokenSettings
        {
            Json = result.GetValue(json),
            Name = result.GetValue(name) ?? "",
            Type = result.GetValue(type) ?? "",
            Url = result.GetValue(url) ?? "",
            Token = result.GetValue(token) ?? "",
            TokenStdin = result.GetValue(tokenStdin),
            TokenEnv = result.GetValue(tokenEnv) ?? "",
        };
    }

    internal static Func<ParseResult, ScmSetSettings> BindSet(Command command)
    {
        var json = Json(command);
        var name = Name(command);
        var (type, url, token, tokenStdin, tokenEnv) = TokenOptions(command, required: false);
        var rename = new Option<string>("--rename") { DefaultValueFactory = _ => "", Description = "A new name" };
        command.Options.Add(rename);
        return result => new ScmSetSettings
        {
            Json = result.GetValue(json),
            Name = result.GetValue(name) ?? "",
            Type = result.GetValue(type) ?? "",
            Url = result.GetValue(url) ?? "",
            Token = result.GetValue(token) ?? "",
            TokenStdin = result.GetValue(tokenStdin),
            TokenEnv = result.GetValue(tokenEnv) ?? "",
            Rename = result.GetValue(rename) ?? "",
        };
    }

    internal static Func<ParseResult, ScmUseSettings> BindUse(Command command)
    {
        var json = Json(command);
        var project = Project(command);
        var name = new Argument<string>("name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "",
            Description = "The SCM this project uses on this machine",
        };
        command.Arguments.Add(name);
        var auto = new Option<bool>("--auto")
        {
            DefaultValueFactory = _ => false,
            Description = "Forget the choice: detect the SCM from the repository's remotes again",
        };
        command.Options.Add(auto);
        command.Validators.Add(result =>
        {
            var named = (result.GetValue(name) ?? "").Length > 0;
            if (named == result.GetValue(auto))
            {
                result.AddError("Give an SCM's name, or --auto to detect it from the remotes again.");
            }
        });
        return result => new ScmUseSettings
        {
            Json = result.GetValue(json),
            Project = result.GetValue(project) ?? ".",
            Name = result.GetValue(name) ?? "",
            Auto = result.GetValue(auto),
        };
    }

    internal static Func<ParseResult, ScmProjectSettings> BindWhich(Command command)
    {
        var json = Json(command);
        var project = Project(command);
        return result => new ScmProjectSettings
        {
            Json = result.GetValue(json),
            Project = result.GetValue(project) ?? ".",
        };
    }

    private static Option<string> Project(Command command)
    {
        var option = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(option);
        return option;
    }

    private static Option<bool> Json(Command command)
    {
        var option = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(option);
        return option;
    }

    private static Argument<string> Name(Command command)
    {
        var argument = new Argument<string>("name")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "The SCM's name",
        };
        command.Arguments.Add(argument);
        return argument;
    }

    private static (
        Option<string> Type,
        Option<string> Url,
        Option<string> Token,
        Option<bool> TokenStdin,
        Option<string> TokenEnv
    ) TokenOptions(Command command, bool required)
    {
        var type = new Option<string>("--type")
        {
            Required = required,
            Description = "The server type: gitlab, forgejo or github",
        };
        type.AcceptOnlyFromAmong("gitlab", "forgejo", "github");
        var url = new Option<string>("--url")
        {
            Required = required,
            Description = "The server's base URL, e.g. https://gitlab.example.com",
        };
        var token = new Option<string>("--token")
        {
            DefaultValueFactory = _ => "",
            Description =
                "The access token (prefer --token-stdin: a token on the command line can stay in shell history)",
        };
        var tokenStdin = new Option<bool>("--token-stdin")
        {
            DefaultValueFactory = _ => false,
            Description = "Read the access token from the first line of standard input",
        };
        var tokenEnv = new Option<string>("--token-env")
        {
            DefaultValueFactory = _ => "",
            Description = "Read the token from this environment variable on each call instead of storing it",
        };
        foreach (var option in new Option[] { type, url, token, tokenStdin, tokenEnv })
        {
            command.Options.Add(option);
        }

        return (type, url, token, tokenStdin, tokenEnv);
    }
}

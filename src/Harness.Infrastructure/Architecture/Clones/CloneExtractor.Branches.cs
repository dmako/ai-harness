using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed partial class CloneExtractor
{
    private static List<CloneBranch> GoBranches(string path, List<CodeToken> tokens)
    {
        List<CloneBranch> output = [];
        HashSet<int> done = [];
        for (var index = 0; index < tokens.Count; index++)
        {
            var choices = tokens[index].Text switch
            {
                "switch" => SwitchChoices(tokens, index),
                "if" when !done.Contains(index) => IfChainChoices(tokens, index, done),
                _ => null,
            };
            if (choices is not null && Branch(path, tokens[index].Line, choices) is { } branch)
            {
                output.Add(branch);
            }
        }
        return output;
    }

    /// <summary>
    /// The branch's choices as name words, without the choices that have none; null when none has any.
    /// </summary>
    private static CloneBranch? Branch(string path, int line, IEnumerable<IReadOnlyList<string>> input)
    {
        var choices = input
            .Select(choice => (IReadOnlyList<string>)choice.SelectMany(CloneContracts.NameWords).ToArray())
            .Where(choice => choice.Count > 0)
            .ToArray();
        if (choices.Length is 0)
        {
            return null;
        }

        var words = choices.SelectMany(choice => choice).Distinct(StringComparer.Ordinal).ToArray();
        return new(path, line, words, choices);
    }

    /// <summary>
    /// The index of the first '{' from <paramref name="from"/> on; the token count when there is none.
    /// </summary>
    private static int BodyStart(List<CodeToken> tokens, int from)
    {
        var body = from;
        while (body < tokens.Count && tokens[body].Text is not "{")
        {
            body++;
        }

        return body;
    }

    /// <summary>
    /// Each value of each top-level case of the switch at <paramref name="at"/>; null when the switch
    /// has no closed body.
    /// </summary>
    private static List<IReadOnlyList<string>>? SwitchChoices(List<CodeToken> tokens, int at)
    {
        var body = BodyStart(tokens, at + 1);
        if (body >= tokens.Count)
        {
            return null;
        }

        var end = CloneLexer.Match(tokens, body);
        if (end < 0)
        {
            return null;
        }

        List<IReadOnlyList<string>> choices = [];
        var depth = 0;
        for (var cursor = body + 1; cursor < end; cursor++)
        {
            if (tokens[cursor].Text is "{")
            {
                depth++;
            }
            else if (tokens[cursor].Text is "}")
            {
                depth--;
            }

            if (depth is 0 && tokens[cursor].Text is "case")
            {
                AddCaseChoices(tokens, cursor + 1, end, choices);
            }
        }
        return choices;
    }

    /// <summary>One choice per comma-separated value from <paramref name="start"/> up to the case's ':'.</summary>
    private static void AddCaseChoices(
        List<CodeToken> tokens,
        int start,
        int end,
        List<IReadOnlyList<string>> choices
    )
    {
        var stop = start;
        while (stop < end && tokens[stop].Text is not ":")
        {
            stop++;
        }

        var part = start;
        for (var item = start; item <= stop; item++)
        {
            if (item == stop || tokens[item].Text is ",")
            {
                choices.Add(ChoicesIn(tokens.GetRange(part, item - part)));
                part = item + 1;
            }
        }
    }

    /// <summary>
    /// The compared values of each condition in the if / else if chain starting at <paramref name="at"/>,
    /// marking every if of the chain as done.
    /// </summary>
    private static List<IReadOnlyList<string>> IfChainChoices(List<CodeToken> tokens, int at, HashSet<int> done)
    {
        List<IReadOnlyList<string>> choices = [];
        var current = at;
        while (current < tokens.Count && tokens[current].Text is "if")
        {
            done.Add(current);
            var body = BodyStart(tokens, current + 1);
            if (body >= tokens.Count)
            {
                break;
            }

            choices.Add(Compared(tokens.GetRange(current + 1, body - current - 1)));
            var end = CloneLexer.Match(tokens, body);
            if (
                end < 0
                || end + 2 >= tokens.Count
                || tokens[end + 1].Text is not "else"
                || tokens[end + 2].Text is not "if"
            )
            {
                break;
            }

            current = end + 2;
        }
        return choices;
    }

    private static List<string> ChoicesIn(List<CodeToken> tokens)
    {
        if (tokens.Any(token => token.Text is "==" or "!=" or "&&" or "||"))
        {
            return Compared(tokens);
        }

        List<string> choices = [];
        var start = 0;
        for (var index = 0; index <= tokens.Count; index++)
        {
            if (index == tokens.Count || tokens[index].Text is "+")
            {
                var choice = Choice(tokens.GetRange(start, index - start));
                if (choice is not null)
                {
                    choices.Add(choice);
                }

                start = index + 1;
            }
        }

        return choices;
    }

    private static List<string> Compared(List<CodeToken> tokens)
    {
        List<string> output = [];
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text is "==" or "!=")
            {
                var start = index - 1;
                if (start >= 2 && tokens[start - 1].Text is ".")
                {
                    start -= 2;
                }

                var end = index + 2;
                if (end < tokens.Count && tokens[end].Text is ".")
                {
                    end += 2;
                }

                if (start >= 0 && Choice(tokens.GetRange(start, index - start)) is { } left)
                {
                    output.Add(left);
                }

                if (end <= tokens.Count && Choice(tokens.GetRange(index + 1, end - index - 1)) is { } right)
                {
                    output.Add(right);
                }
            }
        }

        return output;
    }

    private static string? Choice(List<CodeToken> tokens)
    {
        if (tokens.Count is 1 && tokens[0].Kind is 's')
        {
            var value = tokens[0].Text.Trim('"', '`');
            return value.Length is 0 ? null : value;
        }
        var identifier = tokens.Count switch
        {
            1 when tokens[0].Kind is 'i' => tokens[0].Text,
            3 when tokens[0].Kind is 'i' && tokens[1].Text is "." && tokens[2].Kind is 'i' => tokens[2].Text,
            _ => "",
        };
        return identifier.Length > 0 && char.IsUpper(identifier[0]) ? identifier : null;
    }
}

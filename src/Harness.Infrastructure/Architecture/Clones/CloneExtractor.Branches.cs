using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed partial class CloneExtractor
{
    private static List<CloneBranch> GoBranches(string path, List<CodeToken> tokens)
    {
        List<CloneBranch> output = [];
        HashSet<int> done = [];
        void Add(int at, IEnumerable<IReadOnlyList<string>> input)
        {
            var choices = input.Select(choice => (IReadOnlyList<string>)choice.SelectMany(CloneContracts.NameWords).ToArray()).Where(choice => choice.Count > 0).ToArray();
            if (choices.Length > 0)
            {
                output.Add(new(path, tokens[at].Line, choices.SelectMany(choice => choice).Distinct(StringComparer.Ordinal).ToArray(), choices));
            }
        }
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text is "switch")
            {
                var body = index + 1;
                while (body < tokens.Count && tokens[body].Text is not "{")
                {
                    body++;
                }

                if (body >= tokens.Count)
                {
                    continue;
                }

                var end = CloneLexer.Match(tokens, body);
                if (end < 0)
                {
                    continue;
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

                    if (depth is not 0 || tokens[cursor].Text is not "case")
                    {
                        continue;
                    }

                    var start = cursor + 1;
                    var stop = start;
                    while (stop < end && tokens[stop].Text is not ":")
                    {
                        stop++;
                    }

                    var part = start;
                    for (var item = start; item <= stop; item++)
                    {
                        if (item == stop || tokens[item].Text is ",")
                        { choices.Add(ChoicesIn(tokens.GetRange(part, item - part))); part = item + 1; }
                    }
                }
                Add(index, choices);
            }
            else if (tokens[index].Text is "if" && !done.Contains(index))
            {
                List<IReadOnlyList<string>> choices = [];
                var current = index;
                while (current < tokens.Count && tokens[current].Text is "if")
                {
                    done.Add(current);
                    var body = current + 1;
                    while (body < tokens.Count && tokens[body].Text is not "{")
                    {
                        body++;
                    }

                    if (body >= tokens.Count)
                    {
                        break;
                    }

                    choices.Add(Compared(tokens.GetRange(current + 1, body - current - 1)));
                    var end = CloneLexer.Match(tokens, body);
                    if (end < 0 || end + 2 >= tokens.Count || tokens[end + 1].Text is not "else" || tokens[end + 2].Text is not "if")
                    {
                        break;
                    }

                    current = end + 2;
                }
                Add(index, choices);
            }
        }
        return output;
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
        var identifier = tokens.Count switch { 1 when tokens[0].Kind is 'i' => tokens[0].Text, 3 when tokens[0].Kind is 'i' && tokens[1].Text is "." && tokens[2].Kind is 'i' => tokens[2].Text, _ => "" };
        return identifier.Length > 0 && char.IsUpper(identifier[0]) ? identifier : null;
    }
}

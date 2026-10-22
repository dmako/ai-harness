namespace Harness.Core.Memory;

public sealed record MemoryScope(string Kind, string Value = "")
{
    public static MemoryScope Project { get; } = new("project");
    public static MemoryScope User { get; } = new("user");

    public bool Wider => Kind is "workspace" or "user";

    public int Specificity =>
        Kind switch
        {
            "path" => 100 + Value.Count(character => character is '/'),
            "subsystem" or "branch" => 50,
            "workspace" => -10,
            "user" => -20,
            _ => 0,
        };

    public static MemoryScope Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length is 0 || text is "project")
        {
            return Project;
        }

        if (text is "user")
        {
            return User;
        }

        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException(
                "invalid scope "
                    + MemoryText.Quote(text)
                    + " (want project, path:<dir or file>, subsystem:<name>, branch:<name>, workspace:<name> or user)"
            );
        }

        return new MemoryScope(text[..separator].Trim(), text[(separator + 1)..].Trim()).Normalize();
    }

    public MemoryScope Normalize()
    {
        switch (Kind)
        {
            case "project" or "user":
                if (Value.Length is not 0)
                {
                    throw new ArgumentException($"{Kind} scope takes no value");
                }

                return this;
            case "path":
                var path = Value.Replace('\\', '/');
                if (path.Length is 0 || path.StartsWith('/'))
                {
                    throw new ArgumentException(
                        "path scope must be a path relative to the project root, got " + MemoryText.Quote(Value)
                    );
                }

                List<string> parts = [];
                foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part is ".")
                    {
                        continue;
                    }

                    if (part is "..")
                    {
                        if (parts.Count is 0)
                        {
                            throw new ArgumentException(
                                "path scope must stay inside the project, got " + MemoryText.Quote(Value)
                            );
                        }

                        parts.RemoveAt(parts.Count - 1);
                    }
                    else
                    {
                        parts.Add(part);
                    }
                }

                if (parts.Count is 0)
                {
                    throw new ArgumentException("path scope '.' is the whole project; use project scope");
                }

                return this with
                {
                    Value = string.Join('/', parts),
                };
            case "subsystem" or "branch" or "workspace":
                if (Value.Length is 0)
                {
                    throw new ArgumentException($"{Kind} scope needs a name");
                }

                return this;
            default:
                throw new ArgumentException(
                    "invalid scope kind "
                        + MemoryText.Quote(Kind)
                        + " (want project, path, subsystem, branch, workspace or user)"
                );
        }
    }

    public bool AppliesTo(MemoryScope target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Kind switch
        {
            "project" or "workspace" or "user" => true,
            "path" => target.Kind is "path"
                && (target.Value == Value || target.Value.StartsWith(Value + "/", StringComparison.Ordinal)),
            _ => this == target,
        };
    }

    public override string ToString() => Kind is "project" or "user" ? Kind : Kind + ":" + Value;
}

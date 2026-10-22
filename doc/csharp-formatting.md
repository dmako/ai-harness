# C# formatting

C# uses a **soft 120-character line limit** from the root `.editorconfig`, four
spaces, UTF-8 without BOM, and CRLF line endings. Wrap argument lists, conditions,
initializers, and expressions for readability. String literals, URLs, raw JSON/SQL
fixtures, and other indivisible content may exceed the target; preserve their
values rather than imposing a hard line-length error.

The repository pins CSharpier in `dotnet-tools.json`. Restore it once, then use
the helper to format tracked and non-ignored new C# sources:

```powershell
dotnet tool restore
python scripts/format_csharp.py
python scripts/format_csharp.py --check
```

The helper excludes ignored generated files and non-C# assets/configuration. The
formatter validates its C# syntax-tree changes; do not disable that validation.
Its check command verifies the formatting policy without adding a build-time
dependency or making every line above 120 characters a compiler error.

CSharpier handles layout only. Continue following the project's coding and naming
conventions in `configuration/CodingStyle.editorconfig` and
`configuration/NamingConvention.editorconfig`: pattern matching, `is null` /
`is not null`, and braces around every `if`, `else`, `for`, `while`, and `foreach`
body still apply. Tests use AwesomeAssertions and raw string literals for JSON.

The formatter's [configuration documentation](https://csharpier.com/docs/Configuration)
describes how `max_line_length` supplies a soft wrapping target.

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

/// <summary>
/// What <see cref="CloneExtractor"/> read from each file, kept between syncs (TASK-0105): one file per repository
/// beside the global configuration (cache/clones), holding per path the content hash it was read from and the
/// functions, complexity members and branches extracted. A file whose content (and, for Go, whose package's files)
/// is unchanged is not lexed again. The extractor's version and harness's build are in the header: a new harness
/// build or a change to what is extracted starts the cache over. It can always be deleted.
/// </summary>
public sealed class CloneCache(string directory)
{
    /// <summary>A cache stamped with another build, as a new harness build or extractor version would be.</summary>
    internal CloneCache(string directory, string build)
        : this(directory) => _build = build;

    /// <summary>Bump when extraction changes what it produces for the same file.</summary>
    internal const int ExtractorVersion = 1;

    private const string Magic = "harness-clones";

    private readonly string _build = Build;

    private static readonly string Build =
        ExtractorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "|"
        + (
            typeof(CloneCache)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? ""
        );

    public string Directory { get; } = directory;

    public static CloneCache For(string globalConfigurationPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!, "cache", "clones"));

    /// <summary>The cache file of a repository root; its size on disk is the cache's for that repository.</summary>
    public string PathFor(string root) =>
        Path.Combine(
            Directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(root))))[..32] + ".bin"
        );

    internal static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    /// <summary>
    /// The key of a file read here, the same sync records for it: the SHA-256 of its bytes (text is read as strict
    /// UTF-8, so its bytes are its text's).
    /// </summary>
    internal static string TextHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        IReadOnlyDictionary<string, string>
    > _recorded = new(StringComparer.Ordinal);

    /// <summary>
    /// The content hashes sync recorded for a worktree's files, current for this sync: a file whose recorded hash
    /// is the one its entry was read with is not read again. Files sync does not record are read and hashed.
    /// </summary>
    public void Record(string root, IReadOnlyDictionary<string, string> hashes) => _recorded[Normalize(root)] = hashes;

    /// <summary>The recorded key of each file of the worktree at <paramref name="root"/>, if sync gave them.</summary>
    internal Func<string, string?> Recorded(string root) =>
        _recorded.TryGetValue(Normalize(root), out var hashes) ? path => hashes.GetValueOrDefault(path) : _ => null;

    /// <summary>The entries kept for this repository; none when there is no cache, it is unreadable or outdated.</summary>
    internal Dictionary<string, CloneCacheEntry> Load(string root)
    {
        try
        {
            using var data = new MemoryStream();
            using (var file = File.OpenRead(PathFor(root)))
            using (var deflate = new DeflateStream(file, CompressionMode.Decompress))
            {
                deflate.CopyTo(data);
            }

            var reader = new Reader(data.GetBuffer(), (int)data.Length);
            if (reader.Text() != Magic || reader.Text() != _build || reader.Text() != Normalize(root))
            {
                return new(StringComparer.Ordinal);
            }

            var count = reader.Count();
            var entries = new Dictionary<string, CloneCacheEntry>(count, StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var entry = reader.Entry();
                entries[entry.Path] = entry;
            }
            return entries;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or EndOfStreamException
                        or ArgumentOutOfRangeException
                        or IndexOutOfRangeException
                        or DecoderFallbackException
                        or FormatException
            )
        { /* No usable cache: every file is read again and the cache rewritten. */
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Replaces the repository's cache with these entries; a failure only costs the next sync a full read.</summary>
    internal void Save(string root, IEnumerable<CloneCacheEntry> entries)
    {
        var path = PathFor(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using (var file = File.Create(temporary))
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest))
            using (var buffered = new BufferedStream(deflate, 1 << 16))
            using (var writer = new Writer(buffered))
            {
                writer.Text(Magic);
                writer.Text(_build);
                writer.Text(Normalize(root));
                var list = entries.ToList();
                writer.Count(list.Count);
                foreach (var entry in list)
                {
                    writer.Entry(entry);
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { /* A stray temporary file is harmless. */
            }
        }
    }

    private static string Normalize(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Replace('\\', '/');

    /// <summary>Writes strings once and then by number: token texts repeat across thousands of functions.</summary>
    private sealed class Writer(Stream stream) : IDisposable
    {
        private readonly BinaryWriter _writer = new(stream, Encoding.UTF8, leaveOpen: true);
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);

        public void Count(int count) => _writer.Write7BitEncodedInt(count);

        public void Text(string? text)
        {
            if (text is null)
            {
                _writer.Write7BitEncodedInt(0);
                return;
            }

            if (_strings.TryGetValue(text, out var known))
            {
                _writer.Write7BitEncodedInt(known + 2);
                return;
            }

            _strings[text] = _strings.Count;
            _writer.Write7BitEncodedInt(1);
            _writer.Write(text);
        }

        public void Texts(IReadOnlyList<string> texts)
        {
            Count(texts.Count);
            foreach (var text in texts)
            {
                Text(text);
            }
        }

        public void Entry(CloneCacheEntry entry)
        {
            Text(entry.Path);
            Text(entry.Hash);
            Count(entry.Functions.Count);
            foreach (var function in entry.Functions)
            {
                Function(function);
            }

            Count(entry.Complexity.Count);
            foreach (var member in entry.Complexity)
            {
                Text(member.Path);
                Text(member.Name);
                _writer.Write7BitEncodedInt(member.Line);
                _writer.Write7BitEncodedInt(member.Parameters);
                _writer.Write7BitEncodedInt(member.Complexity);
                _writer.Write7BitEncodedInt(member.Cases);
                Text(member.Language);
            }

            Count(entry.Branches.Count);
            foreach (var branch in entry.Branches)
            {
                Text(branch.Path);
                _writer.Write7BitEncodedInt(branch.Line);
                Texts(branch.Words);
                Count(branch.Choices.Count);
                foreach (var choice in branch.Choices)
                {
                    Texts(choice);
                }
            }
        }

        private void Function(CloneFunction function)
        {
            Text(function.Path);
            Text(function.Name);
            _writer.Write7BitEncodedInt(function.Line);
            _writer.Write7BitEncodedInt(function.End);
            Text(function.Language);
            _writer.Write(function.Override);
            Text(function.Wraps);
            _writer.Write(function.Data);
            Texts(function.Canonical);
            Texts(function.Shape);
            Texts(function.Signature);
            Count(function.Statements.Count);
            foreach (var statement in function.Statements)
            {
                Text(statement.Key);
                Texts(statement.Own);
                Texts(statement.Raw);
                _writer.Write7BitEncodedInt(statement.Line);
                _writer.Write7BitEncodedInt(statement.Size);
                _writer.Write(statement.Idiom);
                _writer.Write(statement.Header);
                _writer.Write(statement.Work);
                Text(statement.Callee);
                _writer.Write(statement.Data);
            }
        }

        public void Dispose() => _writer.Dispose();
    }

    /// <summary>Reads what <see cref="Writer"/> wrote, from the decompressed bytes at once (no per-byte calls).</summary>
    private sealed class Reader(byte[] data, int length)
    {
        private readonly List<string> _strings = [];
        private int _at;

        private int Varint()
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var part = Byte();
                value |= (part & 0x7F) << shift;
                if (part < 0x80)
                {
                    return value;
                }
            }
            throw new InvalidDataException("clone cache: bad number");
        }

        private byte Byte() => _at < length ? data[_at++] : throw new EndOfStreamException();

        private bool Flag() => Byte() is not 0;

        private string String()
        {
            var size = Varint();
            if (size < 0 || size > length - _at)
            {
                throw new EndOfStreamException();
            }

            var text = Encoding.UTF8.GetString(data, _at, size);
            _at += size;
            return text;
        }

        public int Count()
        {
            var count = Varint();
            return count is < 0 or > 10_000_000 ? throw new InvalidDataException("clone cache: bad count") : count;
        }

        /// <summary>A string as written, null included: what went in comes out.</summary>
        public string Text() => NullableText()!;

        private string? NullableText()
        {
            var tag = Varint();
            switch (tag)
            {
                case 0:
                    return null;
                case 1:
                    var text = String();
                    _strings.Add(text);
                    return text;
                default:
                    return _strings[tag - 2];
            }
        }

        private string[] Texts()
        {
            var texts = new string[Count()];
            for (var index = 0; index < texts.Length; index++)
            {
                texts[index] = Text();
            }
            return texts;
        }

        private int Number() => Varint();

        public CloneCacheEntry Entry()
        {
            var path = Text();
            var hash = Text();
            var functions = new CloneFunction[Count()];
            for (var index = 0; index < functions.Length; index++)
            {
                functions[index] = Function();
            }

            var complexity = new ComplexityMember[Count()];
            for (var index = 0; index < complexity.Length; index++)
            {
                complexity[index] = new(Text(), Text(), Number(), Number(), Number(), Number(), Text());
            }

            var branches = new CloneBranch[Count()];
            for (var index = 0; index < branches.Length; index++)
            {
                var branchPath = Text();
                var line = Number();
                var words = Texts();
                var choices = new IReadOnlyList<string>[Count()];
                for (var choice = 0; choice < choices.Length; choice++)
                {
                    choices[choice] = Texts();
                }
                branches[index] = new(branchPath, line, words, choices);
            }
            return new(path, hash, functions, complexity, branches);
        }

        private CloneFunction Function()
        {
            var function = new CloneFunction
            {
                Path = Text(),
                Name = Text(),
                Line = Number(),
                End = Number(),
                Language = Text(),
                Override = Flag(),
                Wraps = NullableText(),
                Data = Flag(),
                Canonical = Texts(),
                Shape = Texts(),
                Signature = Texts(),
            };
            var statements = new CloneStatement[Count()];
            for (var index = 0; index < statements.Length; index++)
            {
                statements[index] = new(
                    Text(),
                    Texts(),
                    Texts(),
                    Number(),
                    Number(),
                    Flag(),
                    Flag(),
                    Flag(),
                    Text(),
                    Flag()
                );
            }
            return function with { Statements = statements };
        }
    }
}

/// <summary>What was extracted from one file whose content (for Go: with its package's files) hashed to <see cref="Hash"/>.</summary>
internal sealed record CloneCacheEntry(
    string Path,
    string Hash,
    IReadOnlyList<CloneFunction> Functions,
    IReadOnlyList<ComplexityMember> Complexity,
    IReadOnlyList<CloneBranch> Branches
);

using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Xunit;

namespace Harness.Unit.Memory;

public sealed class KnowledgeFileTests
{
    private static readonly SharedMemory[] Memories =
    [
        new(
            "mem_b2",
            "convention",
            "CancellationToken is the last parameter",
            MemoryScope.Parse("path:src/Harness.Core"),
            "review",
            "david",
            "2026-10-31T21:00:00Z"
        ),
        new(
            "mem_a1",
            "decision",
            "Quotes \" backslashes \\ tabs\t and\nnew lines survive, as does žluťoučký 🧪",
            MemoryScope.Project
        ),
    ];

    [Fact]
    public void TheSameMemoriesAlwaysGiveTheSameBytesSortedById()
    {
        var text = KnowledgeFile.Format(Memories);

        KnowledgeFile.Format(Memories.Reverse()).Should().Be(text);
        text.Should().StartWith("# Project knowledge shared through Git.");
        text.Should().Contain("version = 1\n");
        text.IndexOf("id = \"mem_a1\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(text.IndexOf("id = \"mem_b2\"", StringComparison.Ordinal));
        text.Should().NotContain("\r");
        text.Should().NotContain("source_ref = \"\"", "empty fields are left out");
    }

    [Fact]
    public void WhatIsWrittenReadsBackTheSame()
    {
        var read = KnowledgeFile.Parse(KnowledgeFile.Format(Memories));

        read.Problems.Should().BeEmpty();
        read.Memories.Should().Equal(Memories.OrderBy(m => m.Id, StringComparer.Ordinal));
    }

    [Fact]
    public void AMemorySharedTwiceIsRefusedWhenWriting() =>
        FluentActions
            .Invoking(() => KnowledgeFile.Format([Memories[0], Memories[0] with { Content = "again" }]))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("memory mem_b2 is shared twice");

    [Fact]
    public void BadEntriesAreReportedWithTheirLineAndSkippedWhileTheOthersLoad()
    {
        const string Text = """
            version = 1

            [[memory]]
            id = "mem_ok"
            type = "decision"
            content = "Kept"

            [[memory]]
            id = "mem_type"
            type = "rumour"
            content = "Unknown type"

            [[memory]]
            id = "mem_empty"
            type = "fact"
            content = "  "

            [[memory]]
            id = "mem_scope"
            type = "warning"
            scope = "planet:mars"
            content = "Bad scope"

            [[memory]]
            id = "mem_ok"
            type = "fact"
            content = "Duplicate id"

            [[memory]]
            id = "mem_wide"
            type = "fact"
            scope = "user"
            content = "Wider than the project"

            [[memory]]
            id = "mem_number"
            type = "fact"
            content = 42

            [[memory]]
            type = "fact"
            content = "No id"

            [[other]]
            name = "x"
            """;

        var read = KnowledgeFile.Parse(Text.Replace("\r\n", "\n", StringComparison.Ordinal));

        read.Memories.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new SharedMemory("mem_ok", "decision", "Kept", MemoryScope.Project));
        read.Problems.Select(p => (p.Line, p.Message.Split(';')[0]))
            .Should()
            .Equal(
                (8, "memory mem_type has an unknown type \"rumour\""),
                (13, "memory mem_empty has no content"),
                (18, "memory mem_scope has an invalid scope \"planet:mars\""),
                (24, "memory mem_ok is listed twice"),
                (
                    29,
                    "memory mem_wide has scope user: only project, path, subsystem and branch knowledge is shared here"
                ),
                (35, "content must be a string"),
                (40, "an entry has no id"),
                (44, "unknown table other: only [[memory]] entries belong here")
            );
    }

    [Theory]
    [InlineData(
        "version = 2\n[[memory]]\nid = \"mem_a\"\ntype = \"fact\"\ncontent = \"x\"\n",
        "written by a newer harness"
    )]
    [InlineData("version = = 1\n", "not valid TOML")]
    public void AFileThisHarnessCannotReadLoadsNothing(string text, string problem)
    {
        var read = KnowledgeFile.Parse(text);

        read.Memories.Should().BeEmpty();
        read.Problems.Should().ContainSingle().Which.Message.Should().StartWith(problem);
    }

    [Fact]
    public void NoFileIsEmptySharedKnowledge() =>
        KnowledgeFile
            .Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "knowledge.toml"))
            .Should()
            .Be(SharedKnowledge.Empty);

    [Fact]
    public void WritingTwiceGivesIdenticalBytesAndTheHashFollowsThem()
    {
        var directory = Directory.CreateTempSubdirectory("harness-knowledge-").FullName;
        try
        {
            var path = KnowledgeFile.In(directory);
            KnowledgeFile.Write(path, Memories);
            var first = File.ReadAllBytes(path);
            var hash = KnowledgeFile.Read(path).Hash;
            KnowledgeFile.Write(path, Memories.Reverse());

            File.ReadAllBytes(path).Should().Equal(first);
            hash.Should().Be(KnowledgeFile.Hash(first)).And.HaveLength(64);
            KnowledgeFile.Read(path).Memories.Should().HaveCount(2);
            Directory.GetFiles(directory).Should().ContainSingle("no temporary file is left behind");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Harness.Infrastructure.Clients;
using Xunit;

namespace Harness.Unit;

public sealed class SerializationTests
{
    [Fact]
    public void BinaryAndEmptyObjectValuesKeepStandardJsonSemantics()
    {
        HarnessJson
            .Serialize(new JsonObjectMap { ["bytes"] = new byte[] { 1, 2, 3 }, ["empty"] = new object() })
            .Should()
            .Be("""{"bytes":"AQID","empty":{}}""");
    }

    [Fact]
    public void TomlDateAndTimeValuesRetainTheFrameworkJsonRepresentation()
    {
        var table = TomlConfigurationEditor.Parse(
            """
            date = 2026-10-22
            local_time = 07:35:00
            local_date_time = 2026-10-22T07:35:00.123
            offset_date_time = 2026-10-22T07:35:00Z
            """
        );
        var expected = JsonSerializer.SerializeToNode(table);
        var actual = HarnessJson.SerializeToNode(table);
        System.Text.Json.Nodes.JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    [Fact]
    public void WireMembersFollowNamingPolicyAndDictionaryKeysRemainData()
    {
        var value = new JsonObjectMap
        {
            ["MemberName"] = "kept",
            ["Optional"] = null,
            ["Payload"] = new Dictionary<string, object?> { ["Exact-Key"] = 1, ["NullValue"] = null },
        };
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        HarnessJson
            .Serialize(value, options)
            .Should()
            .Be("""{"member_name":"kept","payload":{"Exact-Key":1,"NullValue":null}}""");
    }

    [Fact]
    public void LazyHeterogeneousSequencesUseGeneratedMetadata()
    {
        var values = Enumerable.Range(1, 2).Select(index => new JsonObjectMap { ["index"] = index });
        HarnessJson.Serialize(values).Should().Be("""[{"index":1},{"index":2}]""");
        HarnessJson.SerializeToElement(values).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void MissingConfigurationMembersRetainTheirDefaults()
    {
        var configuration = HarnessJson.Deserialize<ProviderConfiguration>("""{"Command":"backend"}""")!;
        configuration.Command.Should().Be("backend");
        configuration.Dir.Should().BeEmpty();
        configuration.Args.Should().BeEmpty();
        configuration.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void UnregisteredModelsFailRatherThanFallingBackToReflection()
    {
        FluentActions
            .Invoking(() => HarnessJson.Serialize(new UnregisteredPayload("value")))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("*No generated JSON metadata*");
    }

    private sealed record UnregisteredPayload(string Value);
}

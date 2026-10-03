using System.Globalization;
using System.Text.Json;
using System.Text.Json.Schema;
using Bizigo.Contracts;
using Bizigo.Api;

namespace Bizigo.UnitTests;

public sealed class TopologyNodeWriteWireTests
{
    [Theory]
    [InlineData(1L, "1")]
    [InlineData(9007199254740993L, "9007199254740993")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    public void Node_write_version_is_exact_decimal_string_and_roundtrips(long version, string expected)
    {
        var node = new TopologyNodeVersion("service:00000000-0000-0000-0000-000000000001",
            TopologyNodeKind.Service, "same name", "A", true, false, version, "9007199254740993");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var raw = JsonSerializer.Serialize(TopologyNodeWriteDto.From(node), options);
            using var json = JsonDocument.Parse(raw);
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("version").ValueKind);
            Assert.Equal(expected, json.RootElement.GetProperty("version").GetString());
            Assert.Equal(node, JsonSerializer.Deserialize<TopologyNodeVersion>(raw, options));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void Node_write_version_schema_is_string_for_generated_clients()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var schema = options.GetJsonSchemaAsNode(typeof(TopologyNodeWriteDto));
        Assert.Equal("string", schema["properties"]!["version"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("9223372036854775807", true)]
    [InlineData("9223372036854775808", false)]
    [InlineData("9007199254740993", true)]
    [InlineData("01", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData("1.0", false)]
    public void Node_mutation_input_accepts_only_lossless_canonical_positive_int64_text(string version, bool accepted)
    {
        var wire = new TopologyNodeMutationDto(TopologyNodeKind.Service, "checkout", "A", true, [], Version: version);
        Assert.Equal(accepted, wire.TryToDomain(out var domain));
        if (!accepted) { Assert.Null(domain); return; }
        Assert.Equal<long?>(long.Parse(version, CultureInfo.InvariantCulture), domain!.Version);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("version").ValueKind);
        Assert.Equal(version, json.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Mutation_input_and_error_response_schema_never_offer_numeric_versions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var input = options.GetJsonSchemaAsNode(typeof(TopologyNodeMutationDto));
        var versionSchema = input["properties"]!["version"]!.ToJsonString();
        Assert.Contains("string", versionSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("integer", versionSchema, StringComparison.Ordinal);
        var edgeVersionSchema = options.GetJsonSchemaAsNode(typeof(TopologyDeclaredEdgeInput))
            ["properties"]!["version"]!.ToJsonString();
        Assert.Contains("string", edgeVersionSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("integer", edgeVersionSchema, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TopologyNodeMutationDto>(
            """{"kind":2,"displayName":"x","ownerGroup":"A","enabled":true,"bindings":[],"version":9223372036854775807}""",
            options));

        var node = new TopologyNodeVersion("service:00000000-0000-0000-0000-000000000001",
            TopologyNodeKind.Service, "x", "A", true, false, long.MaxValue, "9007199254740993");
        var body = new TopologyNodeWriteResultDto(409, TopologyNodeWriteDto.From(node), "Version exhausted");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body, options));
        Assert.Equal("9223372036854775807", json.RootElement.GetProperty("node").GetProperty("version").GetString());
        Assert.Equal(typeof(TopologyNodeWriteDto), typeof(TopologyNodeWriteResultDto).GetProperty("Node")!.PropertyType);
    }
}

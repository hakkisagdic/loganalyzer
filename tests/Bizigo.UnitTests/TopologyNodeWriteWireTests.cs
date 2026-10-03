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
}

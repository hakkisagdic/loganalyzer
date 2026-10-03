using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;

namespace Bizigo.UnitTests;

/// <summary>Exports actual API serializers for the separate generated-TS/Node
/// gate. This is not a replacement for the real HTTP PUT/DELETE registry tests.</summary>
public sealed class TopologyGeneratedWireFixtureTests
{
    [Fact]
    public void Csharp_API_JSON_exports_lossless_versions_nanos_inputs_and_nested_errors()
    {
        var root = Environment.GetEnvironmentVariable("BIZIGO_TOPOLOGY_WIRE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "bizigo-topology-wire");
        Directory.CreateDirectory(root);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        const string source = "service:00000000-0000-0000-0000-000000000001";
        const string target = "service:00000000-0000-0000-0000-000000000002";
        const string nanos = "18446744073709551615";
        foreach (var version in new[] { 1L, 9007199254740993L, long.MaxValue })
        {
            var text = version.ToString(CultureInfo.InvariantCulture);
            var node = new TopologyNodeVersion(source, TopologyNodeKind.Service, "wire", "A", true, false, version, nanos);
            var write = TopologyNodeWriteDto.From(node);
            var input = new TopologyNodeMutationDto(TopologyNodeKind.Service, "wire", "A", true, [], Version: text);
            Assert.True(input.TryToDomain(out var domain)); Assert.Equal<long?>(version, domain!.Version);
            var edge = new TopologyDeclaredEdgeVersion(Guid.Parse("00000000-0000-0000-0000-000000000003"),
                source, target, "depends_on", "A", "A", true, "declared", 1m, text, null,
                nanos, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "creator", "updater");
            Emit(text, "node-write", write);
            Emit(text, "node-input", input);
            Emit(text, "node-error", new TopologyNodeWriteResultDto(409, write, "Version exhausted"));
            Emit(text, "node-read", new TopologyNodePageDto([
                new(source, "service", "wire", "A", true, text, nanos, null)], null, false, null, nanos));
            Emit(text, "edge-write", edge);
            Emit(text, "edge-input", new TopologyDeclaredEdgeInput(source, target, "depends_on", text));
            Emit(text, "edge-error", new TopologyDeclaredEdgeResult(409, edge, "Version exhausted"));
            Emit(text, "edge-read", new TopologyEdgeDto(edge.Id.ToString("D"), source, target,
                "depends_on", "declared", true, 1, "A", "A", "Visible", "18446744073709551614", nanos,
                null, nanos, text));
            // Same decimal text carried by the DELETE query contract. Actual
            // endpoint interpretation at Int64.MaxValue is a separate PG/HTTP oracle.
            Emit(text, "delete-query", new { version = text });
        }
        File.WriteAllText(Path.Combine(root, "sha256.json"), JsonSerializer.Serialize(hashes, options));

        void Emit<T>(string version, string name, T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
            var relative = version + "-" + name + ".json";
            File.WriteAllBytes(Path.Combine(root, relative), bytes);
            hashes.Add(relative, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}

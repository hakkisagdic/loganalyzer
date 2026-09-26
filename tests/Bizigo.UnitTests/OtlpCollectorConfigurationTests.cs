using YamlDotNet.RepresentationModel;

namespace Bizigo.UnitTests;

public sealed class OtlpCollectorConfigurationTests
{
    [Fact]
    public void Shipped_collector_connects_all_three_signals_to_authenticated_persistent_exporter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bizigo.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var reader = File.OpenText(Path.Combine(directory.FullName, "deploy/otel/collector.yaml"));
        var document = new YamlStream(); document.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(document.Documents).RootNode);
        var pipelines = (YamlMappingNode)((YamlMappingNode)root["service"])["pipelines"];
        foreach (var signal in new[] { "logs", "metrics", "traces" })
        {
            var pipeline = (YamlMappingNode)pipelines[signal];
            Assert.Contains("otlp", ((YamlSequenceNode)pipeline["receivers"]).Select(x => x.ToString()));
            Assert.Equal(new[] { "memory_limiter", "batch" }, ((YamlSequenceNode)pipeline["processors"]).Select(x => x.ToString()));
            Assert.Equal("otlp_http/bizigo", Assert.Single(((YamlSequenceNode)pipeline["exporters"]).Select(x => x.ToString())));
        }
        var exporter = (YamlMappingNode)((YamlMappingNode)root["exporters"])["otlp_http/bizigo"];
        Assert.Equal("oauth2client", ((YamlMappingNode)exporter["auth"])["authenticator"].ToString());
        Assert.Equal("file_storage/queue", ((YamlMappingNode)exporter["sending_queue"])["storage"].ToString());
        Assert.Equal("true", ((YamlMappingNode)exporter["retry_on_failure"])["enabled"].ToString());
        Assert.Equal("0", ((YamlMappingNode)exporter["retry_on_failure"])["max_elapsed_time"].ToString());
    }
}

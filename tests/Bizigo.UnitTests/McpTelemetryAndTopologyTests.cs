using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Mcp;
using Bizigo.Mcp.Product;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Query;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

public sealed class McpTelemetryAndTopologyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AccessScope Core => AccessScope.ForGroups("analyst.core", ["network/core"]);
    private static AccessScope Nothing => AccessScope.ForGroups("yeni.kullanici", []);

    private static IServiceScopeFactory Scopes(IScopedQuery query)
    {
        var services = new ServiceCollection();
        services.AddScoped<IScopedQuery>(_ => query);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(-5)]
    public async Task Telemetry_tools_limit_butcesini_asarsa_hata_verir(int invalidLimit)
    {
        var scopes = Scopes(new FakeScopedQuery());
        var metricsList = new MetricsListTool(scopes);
        var metricsDrilldown = new MetricsDrilldownTool(scopes);
        var tracesList = new TracesListTool(scopes);
        var tracesDrilldown = new TracesDrilldownTool(scopes);

        var invocation = McpReadToolFixtures.InvocationOf(
            ("limit", invalidLimit),
            ("source_id", "metric_1"),
            ("trace_id", "0123456789abcdef0123456789abcdef"));

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            metricsList.ExecuteScopedAsync(invocation, Core, Ct).AsTask());

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            metricsDrilldown.ExecuteScopedAsync(invocation, Core, Ct).AsTask());

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            tracesList.ExecuteScopedAsync(invocation, Core, Ct).AsTask());

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            tracesDrilldown.ExecuteScopedAsync(invocation, Core, Ct).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-1)]
    public async Task Topology_tools_limit_butcesini_asarsa_hata_verir(int invalidLimit)
    {
        var scopes = Scopes(new FakeScopedQuery());
        var topologyList = new TopologyListTool(scopes);
        var topologyDrilldown = new TopologyDrilldownTool(scopes);

        var invocation = McpReadToolFixtures.InvocationOf(
            ("limit", invalidLimit),
            ("source_id", "node_1"));

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            topologyList.ExecuteScopedAsync(invocation, Core, Ct).AsTask());

        await Assert.ThrowsAsync<McpToolArgumentException>(() =>
            topologyDrilldown.ExecuteScopedAsync(invocation, Core, Ct).AsTask());
    }

    [Fact]
    public void Tum_yeni_araclar_kapsam_filtresine_tabidir()
    {
        var scopes = Scopes(new FakeScopedQuery());
        ProductReadTool[] tools =
        [
            new MetricsListTool(scopes),
            new MetricsDetailTool(scopes),
            new MetricsDrilldownTool(scopes),
            new TracesListTool(scopes),
            new TracesDetailTool(scopes),
            new TracesDrilldownTool(scopes),
            new TopologyListTool(scopes),
            new TopologyDetailTool(scopes),
            new TopologyDrilldownTool(scopes),
        ];

        foreach (var tool in tools)
        {
            Assert.True(tool.ReadsScopedData, $"{tool.ToolName} kapsam verisi okumalı");
            var rejection = ProductReadTool.ScopeRejection(Nothing, tool.ReadsScopedData);
            Assert.NotNull(rejection);
            Assert.Equal(McpToolError.NotFound, rejection!.Code);
        }
    }

    [Fact]
    public async Task Tum_yeni_araclarin_ornekleri_cikti_semasina_uyar()
    {
        var scopes = Scopes(new FakeScopedQuery());
        ProductReadTool[] tools =
        [
            new MetricsListTool(scopes),
            new MetricsDetailTool(scopes),
            new MetricsDrilldownTool(scopes),
            new TracesListTool(scopes),
            new TracesDetailTool(scopes),
            new TracesDrilldownTool(scopes),
            new TopologyListTool(scopes),
            new TopologyDetailTool(scopes),
            new TopologyDrilldownTool(scopes),
        ];

        foreach (var tool in tools)
        {
            var sample = await tool.SampleAsync(Ct);
            Assert.NotNull(sample);
            var schema = JsonSchema.FromText(tool.OutputSchema.GetRawText());
            var wire = BizigoMcpTool.ToProtocol(sample);
            Assert.NotNull(wire.StructuredContent);
            var eval = schema.Evaluate(wire.StructuredContent.Value, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });
            Assert.True(eval.IsValid, $"{tool.ToolName} çıktı şemasına uymuyor");
        }
    }
}

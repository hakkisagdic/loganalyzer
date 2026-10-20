using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Dağıtım dosyalarının sözleşmesi.</b>
///
/// <para>
/// Compose dosyaları YAML olarak AYRIŞTIRILIYOR, metin olarak aranmıyor: bu
/// depoda bir bekçi, aradığı dizgeyi sağlık denetiminin üstündeki gerekçe
/// yorumunda bulup kusur varken yeşil kaldı. Dockerfile için yorum satırları
/// aramadan önce atılıyor.
/// </para>
/// </summary>
public sealed partial class DeploymentContractTests
{
    private static readonly string[] LiteSettingKeys =
    [
        "LITE_DATA_DIR",
        "LITE_INGEST_TOKEN",
        "LITE_ALLOWED_ORIGINS",
        "LITE_FRAME_ANCESTORS",
        "LITE_ENABLE_DEMO",
    ];

    private static YamlMappingNode LoadYaml(string relativePath)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(RepositoryRoot.File(relativePath.Split('/')))));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlNode node, string key) =>
        (YamlMappingNode)((YamlMappingNode)node).Children[new YamlScalarNode(key)];

    private static YamlNode? Child(YamlNode node, string key) =>
        ((YamlMappingNode)node).Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string Scalar(YamlNode? node) => ((YamlScalarNode)node!).Value ?? string.Empty;

    private static YamlMappingNode CoolifyService()
    {
        var root = LoadYaml("deploy/docker-compose.coolify.lite.yml");
        var services = Map(root, "services");
        Assert.Single(services.Children);
        return Map(services, "loganalyzer-lite");
    }

    [Fact]
    public void Coolify_compose_ana_makine_portu_ve_sabit_ad_tasimiyor()
    {
        var service = CoolifyService();

        Assert.Null(Child(service, "container_name"));

        if (Child(service, "ports") is YamlSequenceNode ports)
        {
            Assert.All(ports.Children, p => Assert.StartsWith("127.0.0.1:", Scalar(p), StringComparison.Ordinal));
        }

        Assert.Contains("5060", ((YamlSequenceNode)Child(service, "expose")!).Children.Select(Scalar));
    }

    /// <summary>
    /// Birim external ve adı birebir <c>loganalyzer-data</c>. Proje önekli yeni
    /// bir birim = sessizce boş başlayan bir dağıtım.
    /// </summary>
    [Fact]
    public void Coolify_compose_external_loganalyzer_data_birimini_bagliyor()
    {
        var root = LoadYaml("deploy/docker-compose.coolify.lite.yml");
        var volume = Map(Map(root, "volumes"), "loganalyzer-data");

        Assert.Equal("true", Scalar(Child(volume, "external")));
        Assert.Equal("loganalyzer-data", Scalar(Child(volume, "name")));

        var mounts = ((YamlSequenceNode)Child(CoolifyService(), "volumes")!).Children.Select(Scalar).ToArray();
        Assert.Equal(["loganalyzer-data:/app/data"], mounts);
    }

    [Fact]
    public void Coolify_compose_coolify_agina_gateway_takma_adiyla_bagli()
    {
        var root = LoadYaml("deploy/docker-compose.coolify.lite.yml");
        Assert.Equal("true", Scalar(Child(Map(Map(root, "networks"), "coolify"), "external")));

        var coolify = Map(Map(CoolifyService(), "networks"), "coolify");
        var aliases = ((YamlSequenceNode)Child(coolify, "aliases")!).Children.Select(Scalar);
        Assert.Contains("loganalyzer-lite", aliases);
    }

    /// <summary>
    /// Traefik etiketi YOK. <c>loglar.&lt;alan&gt;</c>'ın route'u ve BasicAuth'u
    /// bu dosyanın dışında (Traefik dosya sağlayıcısı) tanımlı ve
    /// <c>loganalyzer-lite</c> takma adına gidiyor. Buraya eklenen bir router
    /// aynı <c>Host</c> kuralıyla BasicAuth'suz ikinci bir route olur.
    /// </summary>
    [Fact]
    public void Coolify_compose_traefik_etiketi_tasimiyor()
    {
        var keys = Child(CoolifyService(), "labels") switch
        {
            null => [],
            YamlMappingNode mapping => mapping.Children.Keys.Select(Scalar).ToArray(),
            YamlSequenceNode sequence => sequence.Children.Select(item => Scalar(item).Split('=', 2)[0]).ToArray(),
            var other => throw new InvalidOperationException($"Beklenmeyen labels biçimi: {other.NodeType}"),
        };

        Assert.DoesNotContain(keys, key => key.Trim().StartsWith("traefik.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Coolify_compose_saglik_denetimi_health_ucunu_yokluyor()
    {
        var test = ((YamlSequenceNode)Child(Map(CoolifyService(), "healthcheck"), "test")!).Children.Select(Scalar).ToArray();

        Assert.Equal("CMD", test[0]);
        Assert.Contains("http://127.0.0.1:5060/health", test);
    }

    /// <summary>Ortam değişkenleri yalnızca yer tutucu: depoda hiçbir değer, özellikle belirteç, yok.</summary>
    [Fact]
    public void Coolify_compose_ayarlari_yalnizca_yer_tutucu()
    {
        var environment = Map(CoolifyService(), "environment");
        var values = environment.Children.ToDictionary(p => Scalar(p.Key), p => Scalar(p.Value), StringComparer.Ordinal);

        foreach (var key in LiteSettingKeys)
        {
            Assert.True(values.ContainsKey(key), $"{key} compose'da yok.");
        }

        Assert.Equal("${LITE_INGEST_TOKEN:-}", values["LITE_INGEST_TOKEN"]);
        Assert.Equal("${LITE_ALLOWED_ORIGINS:-}", values["LITE_ALLOWED_ORIGINS"]);
        Assert.Equal("${LITE_FRAME_ANCESTORS:-}", values["LITE_FRAME_ANCESTORS"]);
        Assert.Equal("${LITE_ENABLE_DEMO:-false}", values["LITE_ENABLE_DEMO"]);
        Assert.Equal("/app/data", values["LITE_DATA_DIR"]);
    }

    [Fact]
    public void Yerel_compose_yalnizca_loopback_e_bagli()
    {
        var root = LoadYaml("deploy/docker-compose.lite.yml");
        var ports = (YamlSequenceNode)Child(Map(Map(root, "services"), "loganalyzer-lite"), "ports")!;

        Assert.NotEmpty(ports.Children);
        Assert.All(ports.Children, p => Assert.StartsWith("127.0.0.1:", Scalar(p), StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\s*#.*$", RegexOptions.Multiline)]
    private static partial Regex DockerComment();

    private static string DockerfileWithoutComments() =>
        DockerComment().Replace(File.ReadAllText(RepositoryRoot.File("deploy", "Dockerfile.lite")), string.Empty);

    [Fact]
    public void Dockerfile_net10_alpine_ve_root_olmayan_kullanici()
    {
        var dockerfile = DockerfileWithoutComments();

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine", dockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine", dockerfile, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^USER\s+\$APP_UID\s*$", RegexOptions.Multiline), dockerfile);
        Assert.DoesNotContain("777", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 0700 /app/data", dockerfile, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^HEALTHCHECK .*\n?.*http://127\.0\.0\.1:5060/health", RegexOptions.Multiline), dockerfile);
        Assert.Contains("org.opencontainers.image.revision=\"${GIT_SHA}\"", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>Lite'ın SDK sabitlemesi ve hedef çatısı aynı ana sürümde.</summary>
    [Fact]
    public void Lite_net10_hedefliyor_ve_sdk_sabitlemesi_uyumlu()
    {
        var props = File.ReadAllText(RepositoryRoot.File("src", "LogAnalyzer.Lite", "Directory.Build.props"));
        var globalJson = File.ReadAllText(RepositoryRoot.File("src", "LogAnalyzer.Lite", "global.json"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", props, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"""version"":\s*""10\.0\.\d+"""), globalJson);
        Assert.Equal(".NETCoreApp,Version=v10.0", typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName);
    }
}

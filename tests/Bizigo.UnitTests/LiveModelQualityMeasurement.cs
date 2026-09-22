using System.Globalization;
using Bizigo.Contracts.Security;
using Bizigo.Rca.Models;
using Bizigo.Rca.Reasoning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// T47'nin canlı model tabanı: gerçek OpenAI-uyumlu sağlayıcıdan çıkan serbest
/// metni gerçek <see cref="SentenceBinder"/> üzerinden geçirir.
///
/// <para>
/// Bu bir bekçi değil, ölçüm. Model örnekleme yaptığı için oran üzerinde eşik
/// yok; varsayılan test koşumunda ağ çağrısı da yok. Amaç, atıfsız cümlenin
/// pratikte oluşup oluşmadığını ve oluşuyorsa hangi paydada sayıldığını gerçek
/// model davranışıyla kaydetmek.
/// </para>
///
/// <para>
/// Koşturma:
/// <code>
/// BIZIGO_RCA_LIVE_MODEL=1 \
/// BIZIGO_RCA_LIVE_MODEL_URL=http://127.0.0.1:8091/v1/ \
/// BIZIGO_RCA_LIVE_MODEL_NAME=TinyLlama-1.1B-Chat-v1.0 \
/// dotnet test tests/Bizigo.UnitTests \
///   --filter FullyQualifiedName~LiveModelQualityMeasurement \
///   -l "console;verbosity=detailed"
/// </code>
/// Son rapor <c>$TMPDIR/t47-live-model-quality.log</c>'a da yazılır.
/// </para>
/// </summary>
public sealed class LiveModelQualityMeasurement
{
    private const string SystemPrompt =
        "Use only the supplied evidence. Return exactly two short sentences in Turkish. " +
        "End every sentence with exactly one supplied evidence id in square brackets. " +
        "Do not invent ids or add a heading.";

    private static readonly MeasurementCase[] Cases =
    [
        new(
            "bgp-reset",
            ["ev-asa-1", "ev-asa-2"],
            "Evidence ev-asa-1: edge router rejected the BGP hold timer after a neighbor reset. " +
            "Evidence ev-asa-2: packet loss began in the same minute. Explain the likely cause and impact."),
        new(
            "database-pool",
            ["ev-app-1", "ev-db-1"],
            "Evidence ev-app-1: checkout latency for the application connection pool rose from 8 ms to 4200 ms. " +
            "Evidence ev-db-1: the database reached its configured connection limit at the same time. " +
            "Explain the likely cause and impact."),
        new(
            "dns-timeout",
            ["ev-dns-1", "ev-api-1"],
            "Evidence ev-dns-1: resolver timeouts started immediately after the DNS service restart. " +
            "Evidence ev-api-1: API failures were limited to requests that needed a new name lookup. " +
            "Explain the likely cause and impact."),
    ];

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("BIZIGO_RCA_LIVE_MODEL") == "1";

    [Fact]
    public async Task Gercek_saglayici_atilan_cumle_tabanini_olcer()
    {
        Assert.SkipUnless(
            Enabled,
            "BIZIGO_RCA_LIVE_MODEL=1 gerekiyor — bu bir ölçüm, bekçi değil.");

        var baseUrl = RequiredEnvironmentVariable("BIZIGO_RCA_LIVE_MODEL_URL");
        var model = RequiredEnvironmentVariable("BIZIGO_RCA_LIVE_MODEL_NAME");

        Assert.True(
            baseUrl.EndsWith("/", StringComparison.Ordinal),
            "Taban URL `/` ile bitmeli; aksi hâlde `chat/completions` `/v1` yolunu değiştirir.");

        var boundary = await new ModelBoundaryGate(new DnsEndpointAddressResolver())
            .VerifyAsync(
                new ModelEndpointOptions
                {
                    Name = "t47-live-local",
                    BaseUrl = baseUrl,
                    Model = model,
                    DataBoundary = DataBoundary.Internal,
                    TimeoutSeconds = 120,
                },
                TestContext.Current.CancellationToken);

        Assert.True(boundary.Allowed, boundary.Rejection);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(
            http,
            NullLogger<OpenAiCompatibleModelProvider>.Instance);

        var produced = 0;
        var dropped = 0;
        var fabricatedCitation = 0;
        var report = new List<string>
        {
            "=== T47 · canlı model atılan cümle tabanı ===",
            $"model: {model}",
            $"uç: {baseUrl}",
            $"korpus: {Cases.Length} sabit sentetik kanıt paketi",
            string.Empty,
        };

        foreach (var sample in Cases)
        {
            var request = ModelRequest.Create(
                boundary.Endpoint!,
                PromptContentLevel.Summary,
                RedactedPrompt.Redact(SystemPrompt),
                RedactedPrompt.Redact(sample.UserPrompt));

            Assert.True(request.Allowed, request.Rejection);

            var completion = await provider.CompleteAsync(
                request.Request!,
                TestContext.Current.CancellationToken);

            Assert.True(completion.Ok, completion.Failure);
            Assert.False(
                string.IsNullOrWhiteSpace(completion.Text),
                $"{sample.Name}: model boş metin döndürdü; oran hesaplanamaz.");

            var binding = SentenceBinder.Bind(
                completion.Text,
                sample.VisibleIds.ToHashSet(StringComparer.Ordinal));

            var sampleFabricated = binding.Sentences.Count(
                sentence => !sentence.Bound && sentence.UnresolvedCitations.Count > 0);

            produced += binding.Produced;
            dropped += binding.Dropped;
            fabricatedCitation += sampleFabricated;

            report.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{sample.Name}: produced={binding.Produced}, kept={binding.Kept}, " +
                $"dropped={binding.Dropped}, fabricated={sampleFabricated}, " +
                $"elapsed_ms={completion.Duration.TotalMilliseconds:0}"));
            report.Add("  output: " + completion.Text.ReplaceLineEndings(" ").Trim());
        }

        Assert.True(produced > 0, "Canlı model hiçbir cümle üretmedi; oran hesaplanamaz.");
        Assert.InRange(dropped, 0, produced);
        Assert.InRange(fabricatedCitation, 0, dropped);

        report.Add(string.Empty);
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"TOPLAM: produced={produced}, kept={produced - dropped}, dropped={dropped}, " +
            $"drop_ratio={dropped / (double)produced:0.000}"));
        report.Add(
            dropped == 0
                ? "fabricated_citation_ratio=null (atılan cümle yok)"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"fabricated_citation={fabricatedCitation}, " +
                    $"fabricated_citation_ratio={fabricatedCitation / (double)dropped:0.000}"));
        report.Add(
            "Not: bu sayı bir ürün eşiği ya da anlamsal doğruluk skoru değildir; " +
            "yalnız sağlayıcı + cümle bağlama davranışının ilk canlı tabanıdır.");

        var text = string.Join(Environment.NewLine, report) + Environment.NewLine;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "t47-live-model-quality.log"), text);
        Console.WriteLine(text);
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        Assert.False(string.IsNullOrWhiteSpace(value), $"{name} zorunlu.");
        return value!;
    }

    private sealed record MeasurementCase(
        string Name,
        IReadOnlyList<string> VisibleIds,
        string UserPrompt);
}

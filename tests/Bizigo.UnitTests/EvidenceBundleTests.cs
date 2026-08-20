using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// Kanıt paketinin sözleşmesi (T36).
///
/// <para>
/// İki iddia sınanıyor ve ikisi de <b>geri alınamaz</b>: aynı girdi aynı paketi
/// üretiyor, ve bugün yazılan bir paket yarınki kodla okunabiliyor. İlki
/// olmadan F4 "aynı kanıt üzerinde iki model" karşılaştırması yapamaz — çünkü
/// karşılaştırdığı şeyin aynı kanıt olduğunu bilemez. İkincisi olmadan paketi
/// saklamanın anlamı yok.
/// </para>
/// </summary>
public sealed class EvidenceBundleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    internal static RcaWindow Window() => new()
    {
        From = Now,
        To = Now.AddMinutes(45),
        BaselineFrom = Now.AddDays(-7),
        BaselineTo = Now.AddMinutes(-30),
    };

    internal static EvidenceBundle Bundle(params EvidenceSlice[] slices) => new()
    {
        Id = Guid.Parse("01920000-0000-7000-8000-000000000001"),
        GatheredAt = Now,
        Window = Window(),
        Scope = new BundleScope(["network/core"], IsSystem: false),
        Slices = slices,
        Trust = new WindowTrust(1_204, 0),
    };

    internal static EvidenceSlice Slice(
        string providerId,
        EvidenceStatus status = EvidenceStatus.Gathered,
        long outOfScope = 0,
        params (string Id, double Weight)[] items) => new()
    {
        ProviderId = providerId,
        Kind = providerId.StartsWith("change", StringComparison.Ordinal) ? EvidenceKind.Change : EvidenceKind.Log,
        Status = status,
        OutOfScopeCount = outOfScope,
        Items =
        [
            .. items.Select((item, index) => new EvidenceItem(
                item.Id,
                providerId,
                EvidenceKind.Log,
                Now.AddSeconds(index),
                item.Weight,
                $"özet {item.Id}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = item.Id }))
        ],
    };

    /// <summary>
    /// <b>Kabul kriteri: aynı girdiyle aynı paket.</b>
    ///
    /// <para>
    /// Farklı kimlik, farklı toplama zamanı ve farklı süreler taşıyan iki paket
    /// aynı hash'i veriyor — çünkü hash duvar saati taşıyan hiçbir şeyi
    /// içermiyor. Aynı ayrım <c>ReplayDiff</c>'te de var ve aynı sebeple: her
    /// koşumda değişen bir alanı karşılaştırmaya sokmak gerçek farkları görünmez
    /// kılar.
    /// </para>
    /// </summary>
    [Fact]
    public void Ayni_girdi_ayni_hash()
    {
        var first = Bundle(Slice("logs.first-seen", items: [("a", 1), ("b", 2)]));
        var second = first with
        {
            Id = Guid.NewGuid(),
            GatheredAt = Now.AddHours(9),
            Slices = [first.Slices[0] with { Duration = TimeSpan.FromSeconds(11) }],
        };

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    /// <summary>
    /// <b>Bekçinin kırmızı yanabildiği:</b> kanıt değişince hash değişiyor.
    /// Yukarıdaki test tek başına, hash sabit bir dizgi döndürse de geçerdi.
    /// </summary>
    [Theory]
    [InlineData("payload")]
    [InlineData("weight")]
    [InlineData("status")]
    [InlineData("out-of-scope")]
    [InlineData("window")]
    [InlineData("scope")]
    [InlineData("trust")]
    public void Kanit_degisince_hash_degisiyor(string what)
    {
        var original = Bundle(Slice("logs.first-seen", items: [("a", 1.0)]));
        var slice = original.Slices[0];
        var item = slice.Items[0];

        var changed = what switch
        {
            "payload" => original with
            {
                Slices = [slice with { Items = [item with { Payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "başka" } }] }],
            },
            "weight" => original with { Slices = [slice with { Items = [item with { Weight = 2.0 }] }] },
            "status" => original with { Slices = [slice with { Status = EvidenceStatus.Empty }] },
            "out-of-scope" => original with { Slices = [slice with { OutOfScopeCount = 342 }] },
            "window" => original with { Window = original.Window with { To = Now.AddMinutes(46) } },
            "scope" => original with { Scope = new BundleScope(["network/edge"], IsSystem: false) },
            "trust" => original with { Trust = new WindowTrust(1_204, 7) },
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        Assert.NotEqual(original.ContentHash, changed.ContentHash);
    }

    /// <summary>
    /// Sağlayıcı <b>sırası</b> hash'i etkilemiyor: sağlayıcılar paralel koşuyor
    /// ve kayıt sırası DI'nin insafında. Etkileseydi, aynı girdi iki koşumda
    /// farklı hash üretebilirdi ve determinizm iddiası sessizce yanlış olurdu.
    /// </summary>
    [Fact]
    public void Saglayici_sirasi_hashi_etkilemiyor()
    {
        var a = Slice("logs.first-seen", items: [("a", 1)]);
        var b = Slice("change.feed", items: [("c", 1)]);

        Assert.Equal(Bundle(a, b).ContentHash, Bundle(b, a).ContentHash);
    }

    /// <summary>
    /// Dilim <b>içindeki</b> satır sırası hash'i etkiliyor — sıra sinyalin
    /// kendisi (yayılma zaman sıralı). Etkilemeseydi, sıralaması bozulmuş bir
    /// yayılma sinyali "aynı kanıt" sayılırdı.
    /// </summary>
    [Fact]
    public void Dilim_ici_satir_sirasi_hashi_etkiliyor()
    {
        var forward = Bundle(Slice("logs.propagation", items: [("a", 1), ("b", 1)]));
        var reversed = forward with
        {
            Slices = [forward.Slices[0] with { Items = [.. forward.Slices[0].Items.Reverse()] }],
        };

        Assert.NotEqual(forward.ContentHash, reversed.ContentHash);
    }

    [Fact]
    public void Gidis_donus_paketi_koruyor()
    {
        var original = Bundle(
            Slice("logs.first-seen", items: [("a", 1.5)]),
            Slice("change.feed", EvidenceStatus.NeverFed, outOfScope: 342));

        var round = BundleSerializer.Deserialize(BundleSerializer.Serialize(original));

        Assert.Equal(original.ContentHash, round.ContentHash);
        Assert.Equal(original.Id, round.Id);
        Assert.Equal(EvidenceStatus.NeverFed, round.Slices.Single(s => s.ProviderId == "change.feed").Status);
        Assert.Equal(342, round.OutOfScopeCount);
        Assert.Equal(original.Trust, round.Trust);
    }

    /// <summary>
    /// JSON adlandırma <c>snake_case</c> (depo kuralı §8). Saklanan bir belgede
    /// camelCase'e kayma geri dönülemez: geçmiş paketler bir daha okunamaz.
    /// </summary>
    [Fact]
    public void Json_snake_case()
    {
        var json = BundleSerializer.Serialize(Bundle(Slice("logs.first-seen", items: [("a", 1)])));

        Assert.Contains("\"gathered_at\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"baseline_from\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gatheredAt\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Sürüm donması sınanıyor:</b> diske bugün yazılmış bir belge, bugünkü
    /// kodla okunuyor.
    ///
    /// <para>
    /// Bu metin bir <b>fixture</b>, üretilen bir değer değil — kaynakta elle
    /// duruyor. Üretilseydi test hiçbir şey kanıtlamazdı: kod ile fixture aynı
    /// anda değişir ve "eski paket okunabiliyor mu" sorusu hep evet cevaplanırdı.
    /// Sürüm artarsa bu fixture <b>kalır</b> ve yenisi eklenir.
    /// </para>
    /// </summary>
    [Fact]
    public void Surum_1_belgesi_bugunku_kodla_okunuyor()
    {
        const string V1 = """
            {"id":"01920000-0000-7000-8000-000000000001",
             "gathered_at":"2026-08-20T14:00:00+00:00",
             "schema_version":1,
             "window":{"from":"2026-08-20T14:00:00+00:00","to":"2026-08-20T14:45:00+00:00",
                       "baseline_from":"2026-08-13T14:00:00+00:00","baseline_to":"2026-08-20T13:30:00+00:00",
                       "owner_groups":[],"source_ids":[]},
             "scope":{"owner_groups":["network/core"],"is_system":false},
             "slices":[{"provider_id":"logs.first-seen","kind":"log","status":"gathered","detail":"",
                        "items":[{"id":"first-seen:42","provider_id":"logs.first-seen","kind":"log",
                                  "timestamp":"2026-08-20T14:02:00+00:00","weight":3,
                                  "summary":"ilk kez görüldü","payload":{"signature_hash":"42"},
                                  "drilldown":null}],
                        "out_of_scope_count":0,"truncated":false,"duration":"00:00:00.2400000"}],
             "trust":{"total_events":1204,"unreliable_time_events":0,"measured":true}}
            """;

        var bundle = BundleSerializer.Deserialize(V1);

        Assert.Equal(1, bundle.SchemaVersion);
        Assert.Equal("logs.first-seen", bundle.Slices[0].ProviderId);
        Assert.Equal(3.0, bundle.Slices[0].Items[0].Weight);
        Assert.Equal("42", bundle.Slices[0].Items[0].Payload["signature_hash"]);
        Assert.Equal(1_204, bundle.Trust.TotalEvents);

        // Rapor da kurulabiliyor — okunabilirlik "çözülüyor" değil, "işe yarıyor".
        Assert.Single(DeterministicReport.From(bundle).Findings);
    }

    /// <summary>
    /// <b>Ölçülmedi</b> ile <b>sıfır</b> farklı şeyler.
    /// <see cref="WindowTrust.Unmeasured"/> sıfır sayı taşıyor ama
    /// <c>HasUnreliableTime</c> ve <c>UnreliableRatio</c> onu sıfır gibi
    /// göstermiyor; rapor da ikisini farklı yazıyor.
    /// </summary>
    [Fact]
    public void Olculmedi_ile_sifir_ayri()
    {
        Assert.False(WindowTrust.Unmeasured.Measured);
        Assert.Null(WindowTrust.Unmeasured.UnreliableRatio);
        Assert.False(WindowTrust.Unmeasured.HasUnreliableTime);

        var measuredZero = new WindowTrust(1_000, 0);
        Assert.True(measuredZero.Measured);
        Assert.Equal(0.0, measuredZero.UnreliableRatio);
        Assert.False(measuredZero.HasUnreliableTime);

        Assert.NotEqual(
            Bundle().ContentHash,
            (Bundle() with { Trust = WindowTrust.Unmeasured }).ContentHash);
    }

    /// <summary>
    /// Kapsam dışı sayım <b>yalnızca sayı</b>: paketin hiçbir yerinde kapsam
    /// dışı kayıtların içeriği yok (K17, RCA §3.2).
    /// </summary>
    [Fact]
    public void Kapsam_disi_sayim_icerik_sizdirmiyor()
    {
        var bundle = Bundle(Slice("change.feed", outOfScope: 342, items: [("c", 1)]));
        var json = BundleSerializer.Serialize(bundle);

        Assert.Equal(342, bundle.OutOfScopeCount);
        Assert.Contains("\"out_of_scope_count\":342", json, StringComparison.Ordinal);

        // Sayının yanında taşınabilecek her şey yok: grup adı, kimlik, gövde.
        Assert.DoesNotContain("out_of_scope_items", json, StringComparison.Ordinal);
        Assert.DoesNotContain("out_of_scope_groups", json, StringComparison.Ordinal);
    }
}

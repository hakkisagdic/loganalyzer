using System.Text.RegularExpressions;
using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// Simülatör fixture'ları ürünün zincirini <b>gerçekten</b> çalıştırıyor mu
/// (FS · S01).
///
/// <para>
/// <c>DeviceConfigTests</c>'in yerine geçmiyor, yanına geliyor. O testler küçük
/// ve hedefli girdilerle tek tek davranışları izole ediyor; onları fixture
/// dosyalarına çevirmek izolasyonu kaybettirirdi. Buradaki soru başka:
/// <b>simülatörün ürettiği config'ler o davranışları tetikliyor mu?</b>
/// </para>
///
/// <para>
/// Bu ayrım olmadan fixture'lar sessizce anlamsız kalabilirdi: bir senaryo
/// dosyası yanlışlıkla baseline'ın kopyası olsa hiçbir test bunu söylemezdi ve
/// S02–S06 boyunca "fark yok" sonucu doğru sanılırdı.
/// </para>
/// </summary>
public sealed partial class SimulatorFixtureChainTests
{
    private static string ProfileDirectory =>
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    /// <summary>
    /// Senaryo adının <b>anlamı</b>. Ad bir etiket değil bir iddia: `gurultu`
    /// "fark ÇIKMAMALI" demek. Beklenti burada yazılı olmasaydı, senaryo adları
    /// zamanla anlamsız dizgelere dönerdi.
    /// </summary>
    public enum Beklenti
    {
        /// <summary>Normalizer gürültüyü eliyor ya da çoklu-küme farkı sıra değişimini yutuyor.</summary>
        FarkYok,

        /// <summary>Gerçek bir değişiklik; fark çıkmalı.</summary>
        FarkVar,

        /// <summary>Fark çıkmalı ama gizli değer hiçbir çıktıda görünmemeli.</summary>
        FarkVarSirYok,
    }

    public static TheoryData<string, string, Beklenti> Senaryolar() => new()
    {
        { "fw-ankara-01", "gurultu", Beklenti.FarkYok },
        { "fw-ankara-01", "kural-eklendi", Beklenti.FarkVar },
        { "fw-ankara-01", "sir-dondu", Beklenti.FarkVarSirYok },
        { "asa-dc-01", "gurultu", Beklenti.FarkYok },
        { "asa-dc-01", "sir-dondu", Beklenti.FarkVarSirYok },
        { "rb-sube-07", "gurultu", Beklenti.FarkYok },
        { "rb-sube-07", "kural-eklendi", Beklenti.FarkVar },
        { "fw-izmir-01", "cihaz-yeniden-yazdi", Beklenti.FarkYok },
    };

    [Theory]
    [MemberData(nameof(Senaryolar))]
    public async Task Senaryo_urunun_zincirinde_beklenen_sonucu_uretiyor(
        string profileId,
        string scenario,
        Beklenti beklenti)
    {
        var profile = SimulatorProfileStore
            .LoadAll(ProfileDirectory, RepositoryLayout.Root)
            .Single(r => r.Profile.Id == profileId)
            .Profile;

        var vendor = $"{profile.Vendor}.{profile.Product}";
        var target = Target(vendor);
        var ct = TestContext.Current.CancellationToken;

        var taban = await new SimulatedDeviceTransport(profile, ProfileDirectory)
            .RunAsync(target, ["show"], ct);

        var sonra = await new SimulatedDeviceTransport(profile, ProfileDirectory, scenario)
            .RunAsync(target, ["show"], ct);

        Assert.True(taban.Ok, taban.Error);
        Assert.True(sonra.Ok, sonra.Error);

        // Fixture'ın kendisi anlamlı mı: senaryo dosyası baseline'ın kopyası
        // olsaydı aşağıdaki her iddia yanlış sebeple geçerdi.
        Assert.NotEqual(taban.Output, sonra.Output);

        var oncekiNormal = ConfigNormalizer.Normalize(vendor, taban.Output);
        var sonrakiNormal = ConfigNormalizer.Normalize(vendor, sonra.Output);
        var diff = ConfigDiff.Compare(oncekiNormal, sonrakiNormal);

        // Normalize edilmiş hâl `ConfigLine` listesi; metin iddiaları için
        // BÖLÜM ADI ve SATIR birlikte taranıyor — sır ikisinden birine sızabilir.
        var oncekiMetin = Duzlestir(oncekiNormal);
        var sonrakiMetin = Duzlestir(sonrakiNormal);

        switch (beklenti)
        {
            case Beklenti.FarkYok:
                Assert.False(
                    diff.HasChanges,
                    $"{profileId}/{scenario}: fark çıkmamalıydı ama " +
                    $"{diff.Added} eklendi, {diff.Removed} silindi. " +
                    "Ya normalizer gürültüyü elemiyor, ya fixture gürültüden fazlasını değiştiriyor.");
                break;

            case Beklenti.FarkVar:
                Assert.True(
                    diff.HasChanges,
                    $"{profileId}/{scenario}: gerçek bir değişiklik vardı ama fark çıkmadı.");
                break;

            case Beklenti.FarkVarSirYok:
                Assert.True(diff.HasChanges, $"{profileId}/{scenario}: anahtar rotasyonu fark üretmeliydi.");

                // Rotasyon GÖRÜNMELİ ama değer görünmemeli: maskeleme siliyor
                // değil maskeliyor, yani özet değişiyor ve sır hiçbir yere
                // yazılmıyor.
                Assert.Contains("<gizli:", sonrakiMetin, StringComparison.Ordinal);

                foreach (var sir in HamSirlar(taban.Output).Concat(HamSirlar(sonra.Output)))
                {
                    Assert.DoesNotContain(sir, oncekiMetin, StringComparison.Ordinal);
                    Assert.DoesNotContain(sir, sonrakiMetin, StringComparison.Ordinal);
                    Assert.DoesNotContain(sir, diff.ToString(), StringComparison.Ordinal);

                    foreach (var section in diff.Sections)
                    {
                        Assert.DoesNotContain(sir, section.Section, StringComparison.Ordinal);
                    }
                }

                break;
        }
    }

    private static string Duzlestir(IReadOnlyList<ConfigLine> lines) =>
        string.Join("\n", lines.Select(line => line.Section + " | " + line.Text));

    /// <summary>
    /// Ham config'teki gizli değerler — <b>fixture'dan okunuyor</b>, teste
    /// yazılmıyor. Yazılsaydı fixture değiştiği gün test eski değeri arar ve
    /// yeni sızıntıyı göremezdi.
    /// </summary>
    private static IEnumerable<string> HamSirlar(string raw)
    {
        foreach (Match match in SecretPattern().Matches(raw))
        {
            var value = match.Groups["value"].Value;

            if (value.Length >= 8)
            {
                yield return value;
            }
        }
    }

    [GeneratedRegex(@"(?:psksecret ENC|pre-shared-key)\s+(?<value>\S+)")]
    private static partial Regex SecretPattern();

    private static DeviceTarget Target(string vendor) => new()
    {
        Vendor = vendor,
        Host = "127.0.0.1",
        Username = "bizigo-ro",
        Credential = "kullanilmiyor",
    };
}

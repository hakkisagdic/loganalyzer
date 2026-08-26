using System.Reflection;
using System.Text;
using Bizigo.Contracts.Security;
using Bizigo.Devices;
using Bizigo.Simulators;

namespace Bizigo.UnitTests;

/// <summary>
/// Prompt redaksiyon tabanı (T41) — F4'ün önkoşulu.
///
/// <para>
/// Docker yok, konteyner yok: bu paket ajan sınırında koşuyor (§2). Sınanan şey
/// keşif ve ikame; LLM'in kendisi değil.
/// </para>
///
/// <para>
/// <b>Bu paketin en kritik testi <see cref="Altin_korpusta_yanlis_pozitif_yok"/>
/// DEĞİL.</b> Yanlış pozitif ölçümü tek başına yeşil yanarken kapı hiçbir şey
/// bulmuyor olabilir — ticket §3'ün tuzağı tam olarak bu. İki iddia birbirini
/// tutuyor: <see cref="Kapi_sir_tasiyan_fixturelarda_sirri_buluyor"/> "gerçekten
/// buluyor" der, altın korpus testi "fazladan bulmuyor" der. Yalnızca biri
/// sağlanırsa kapı yoktur.
/// </para>
/// </summary>
public sealed class RedactionGateTests
{
    private const string SahteIsaret = "SAHTE";
    private const string BeklenenOnek = "# BEKLENEN:";

    private static string ProfileDirectory =>
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    public static TheoryData<string> SirTasiyanFixtureler() => new()
    {
        "asa-dc-01", "fw-ankara-01", "rb-sube-07", "lb-web-01",
    };

    private static string FixturePath(string profil) =>
        Path.Combine(ProfileDirectory, "profiller", profil, "sir-tasiyan.log");

    /// <summary>
    /// Fixture'ın iki yarısı: <c>#</c> ile başlayan başlık (beklenti bildirimi)
    /// ve gerçek log satırları. Kapıya yalnızca ikincisi giriyor — başlık log
    /// değil, fixture'ın kendi hakkındaki beyanı.
    /// </summary>
    private static (string Payload, IReadOnlyList<string> Beklenen) Fixture(string profil)
    {
        var lines = File.ReadAllLines(FixturePath(profil));

        var beklenen = lines
            .Where(l => l.StartsWith(BeklenenOnek, StringComparison.Ordinal))
            .Select(l => l[BeklenenOnek.Length..].Trim())
            .ToArray();

        var payload = string.Join('\n', lines.Where(l => !l.StartsWith('#')));

        return (payload, beklenen);
    }

    // ------------------------------------------------------- kriter 3, 4

    /// <summary>
    /// Kriter 3: kapı <c>sir-dondu</c> konvansiyonundaki sahte sır
    /// fixture'larında sırrı buluyor ve maskeliyor.
    ///
    /// <para>
    /// <b>Beklenen değerler teste yazılmıyor, dosyadan okunuyor</b> —
    /// <c>SimulatorFixtureChainTests.HamSirlar</c>'ın aynı gerekçesi: yazılsaydı
    /// fixture değiştiği gün test eski değeri arar ve yeni sızıntıyı göremezdi.
    /// </para>
    ///
    /// <para>
    /// Kriter 4 (kapı kırmızı yanabiliyor) bu testin üstünde ölçüldü: kapı
    /// devre dışı bırakıldığında ve sahte sır biçimi değiştirildiğinde düştü.
    /// Ölçüm ticket §11'de.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(SirTasiyanFixtureler))]
    public void Kapi_sir_tasiyan_fixturelarda_sirri_buluyor(string profil)
    {
        var (payload, beklenen) = Fixture(profil);

        Assert.NotEmpty(beklenen);

        var sonuc = RedactedPrompt.Redact(payload);

        foreach (var sir in beklenen)
        {
            // Beyan çürümüş olabilir: bildirilen değer dosyada gerçekten
            // geçmiyorsa "maskelendi" iddiası boş bir iddiadır.
            Assert.Contains(sir, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(sir, sonuc.Text, StringComparison.Ordinal);
        }

        // Bildirilmemiş ama işaretli bir değer kaldıysa da sızıntıdır.
        Assert.DoesNotContain(SahteIsaret, sonuc.Text, StringComparison.Ordinal);

        // Maskeleme SecretRedactor üzerinden (kriter 5): işaret onun işareti.
        Assert.Contains(SecretRedactor.Mask, sonuc.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- kriter 10

    /// <summary>
    /// Kriter 10: depoya gerçek bir sır girmedi.
    ///
    /// <para>
    /// Her bildirilen değer ya düz metninde ya da base64url parçalarından
    /// birinin çözümünde <c>SAHTE</c> işaretini taşıyor. JWT'nin gövdesi
    /// okunabilir bir dizge değil, o yüzden çözülerek bakılıyor — aksi hâlde
    /// bu kapı JWT'ye hiç bakmıyor olurdu ve biri gerçek bir belirteç
    /// yapıştırdığında hiçbir şey kırmızı yanmazdı.
    /// </para>
    ///
    /// <para>
    /// Depoda <c>gitleaks</c>/<c>trufflehog</c> türü bir dış tarama adımı
    /// <b>yok</b> — arandı, bulunamadı. Fixture disiplininin tek savunması bu
    /// test.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(SirTasiyanFixtureler))]
    public void Fixture_degerlerinin_hepsi_sahte(string profil)
    {
        var (_, beklenen) = Fixture(profil);

        foreach (var sir in beklenen)
        {
            Assert.True(
                Isaretli(sir),
                $"{profil}: '{sir[..Math.Min(24, sir.Length)]}…' üzerinde `{SahteIsaret}` işareti yok — " +
                "gerçek bir sır mı yapıştırıldı?");
        }
    }

    private static bool Isaretli(string value)
    {
        if (value.Contains(SahteIsaret, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var segment in value.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Base64UrlCoz(segment) is { } cozulmus
                && cozulmus.Contains(SahteIsaret, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Base64UrlCoz(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');

        return Convert.TryFromBase64String(normalized, new byte[normalized.Length], out var written)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(normalized), 0, written)
            : null;
    }

    // ------------------------------------------------------- §5 bekçisi

    /// <summary>
    /// <b>Sır taşıyan fixture tel üzerinden basılmıyor.</b>
    ///
    /// <para>
    /// Simülatör profillerinin <c>syslog.samples</c> alanı altın örnek
    /// dosyalarını işaret ediyor ve simülatör onları gerçekten basıyor. Bir
    /// <c>sir-tasiyan.log</c> oraya girerse sahte de olsa bir sır tel üzerine
    /// çıkar — ve kimse fark etmez, çünkü hiçbir şey hata vermez.
    /// </para>
    /// </summary>
    [Fact]
    public void Sir_tasiyan_fixture_hicbir_profilin_ornekleri_arasinda_degil()
    {
        var ihlal = SimulatorProfileStore
            .LoadAll(ProfileDirectory, RepositoryLayout.Root)
            .SelectMany(r => (r.Profile.Syslog?.Samples ?? []).Select(s => (r.Profile.Id, Sample: s)))
            .Where(x => x.Sample.Contains("sir-tasiyan", StringComparison.Ordinal))
            .Select(x => $"{x.Id} → {x.Sample}")
            .ToArray();

        Assert.True(
            ihlal.Length == 0,
            "Sır taşıyan fixture bir profilin syslog örnekleri arasında:\n  " + string.Join("\n  ", ihlal));
    }

    // ------------------------------------------------------- kriter 7

    /// <summary>
    /// Kriter 7: 87 satırlık altın korpusta yanlış pozitif ölçüldü.
    ///
    /// <para>
    /// Beklenen <b>0/87</b>. Ticket §3 aynı ölçümü <i>config</i> deseniyle
    /// yapmıştı; buradaki desen log çapalı, yani ölçüm tekrarlanmak zorunda —
    /// çapa değişmişse sayı da değişebilir.
    /// </para>
    ///
    /// <para>
    /// Üç eşleşen satır (<c>reason="passwd_invalid"</c>,
    /// <c>msg="… invalid password"</c>) düz anahtar kelime taramasında çıkıyor
    /// ama atama deseninde çıkmıyor: anahtar kelimeden sonra <c>_</c> ya da
    /// <c>"</c> geliyor, ayırıcı gelmiyor. Maskelenselerdi korunan bir şey
    /// olmazdı — o satırlarda "password" bir <i>hata sebebi</i>, bir
    /// <i>atama</i> değil — ve RCA'nın ihtiyacı olan sebep bilgisi giderdi.
    /// </para>
    /// </summary>
    [Fact]
    public void Altin_korpusta_yanlis_pozitif_yok()
    {
        var satirlar = Directory
            .EnumerateFiles(RepositoryLayout.CatalogParserDirectory, "*.log", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        Assert.True(satirlar.Length >= 80, $"Altın korpus beklenenden küçük: {satirlar.Length} satır.");

        var maskelenen = satirlar
            .Select(l => (Satir: l, Sonuc: RedactedPrompt.Redact(l)))
            .Where(x => x.Sonuc.MaskedValues > 0)
            .ToArray();

        Assert.True(
            maskelenen.Length == 0,
            $"Altın korpusun {satirlar.Length} satırından {maskelenen.Length} tanesi maskelendi. " +
            "Sayı 0 değilse GEREKÇESİ yazılmalı (kriter 7):\n  " +
            string.Join("\n  ", maskelenen.Take(10).Select(x => x.Satir)));
    }

    // ------------------------------------------------------- kriter 2

    /// <summary>
    /// Kriter 2: gölge katman çıktıyı <b>değiştirmiyor</b>.
    ///
    /// <para>
    /// Gölge katmanın sessizce maskelemeye başlaması tam olarak bu deponun
    /// sessiz-yanlış sınıfı olurdu: prompt daralır, bağlam gider, ve hiçbir
    /// sayaç sebebini söylemez. Girdi yüksek entropili ama <b>sır sözdizimi
    /// taşımayan</b> belirteçlerden oluşuyor — C ve B'nin görmediği, A'nın
    /// gördüğü şeyler.
    /// </para>
    /// </summary>
    [Fact]
    public void Golge_katman_ciktiyi_degistirmiyor()
    {
        const string metin =
            "Sep  3 11:20:00 asa-dc-01 : %ASA-6-302013: session 9f8c1b2a7d4e6f0a3c5b8d1e4f7a2c9b " +
            "digest sha256:3b1f8e2d9c7a4b6e0f5d8a1c3e7b9d2f4a6c8e0b1d3f5a7c9e1b3d5f7a9c1e3b\n";

        var sonuc = RedactedPrompt.Redact(metin);

        Assert.Equal(metin, sonuc.Text);
        Assert.Equal(0, sonuc.MaskedValues);
        Assert.DoesNotContain(SecretRedactor.Mask, sonuc.Text, StringComparison.Ordinal);

        // Ama SAYIYOR: aksi hâlde bu test "A hiç çalışmıyor" hâlinde de geçerdi.
        Assert.True(
            sonuc.ShadowCandidates > 0,
            "Gölge katman hiçbir aday saymadı — girdi yüksek entropili belirteç taşıyor. " +
            "Katman sessizce kapalıysa bu test 'A maskelemiyor' iddiasını boş yere geçirir.");
    }

    // ------------------------------------------------------- kriter 8

    /// <summary>
    /// Kriter 8: gölge sayıları <b>sıfırken de</b> yazılıyor, eşikle birlikte.
    ///
    /// <para>
    /// Gizlenen bir sıfır "henüz ölçülmedi" ile "ölçüldü, sıfır" farkını siler.
    /// Eşiksiz bir sayı da eşiği değiştiren bir sonraki turda kendi geçmişiyle
    /// karşılaştırılamaz hâle gelir.
    /// </para>
    /// </summary>
    [Fact]
    public void Golge_sayilari_sifirken_de_yaziliyor()
    {
        var sonuc = RedactedPrompt.Redact("Sep  3 11:04:12 asa-dc-01 : %ASA-6-302014: Teardown TCP connection 1\n");

        Assert.Equal(0, sonuc.ShadowCandidates);

        var alanlar = sonuc.EvidenceFields();

        foreach (var ad in new[]
        {
            "redaction_masked_values",
            "redaction_shadow_candidates",
            "redaction_shadow_ratio",
            "redaction_shadow_evaluated_tokens",
            "redaction_shadow_total_tokens",
            "redaction_shadow_entropy_threshold",
            "redaction_shadow_min_token_length",
        })
        {
            Assert.True(alanlar.ContainsKey(ad), $"`{ad}` yayılmıyor.");
        }

        Assert.Equal(0, alanlar["redaction_shadow_candidates"]);
        Assert.Equal(0d, alanlar["redaction_shadow_ratio"]);
        Assert.Equal(RedactedPrompt.DefaultEntropyThreshold, alanlar["redaction_shadow_entropy_threshold"]);

        // Payda söylenmeden oran okunamaz — boş bir metinde bile yazılıyor.
        Assert.Equal(0, RedactedPrompt.Redact(null).EvidenceFields()["redaction_shadow_evaluated_tokens"]);
    }

    // ------------------------------------------------------- kriter 1

    /// <summary>
    /// Kriter 1: prompt'a giden metin <b>tek</b> bir kapıdan geçiyor ve bu
    /// kapıyı atlayan ikinci bir yol yok.
    ///
    /// <para>
    /// İddianın birinci yarısını derleyici tutuyor: <see cref="RedactedPrompt"/>
    /// yapıcısı <c>private</c>, dolayısıyla derlemenin <b>dışından</b> bir örnek
    /// üretmek imkânsız. İkinci yarısı burada: aynı derlemenin içinden ikinci
    /// bir üretici eklenirse bu test düşer.
    /// </para>
    ///
    /// <para>
    /// <b>Testin sınırı yazılı olsun:</b> "kapıyı atlayan yol yok" iddiası
    /// <i>bu tipi üreten yol</i> hakkında. F4 prompt'unu <c>string</c> alarak
    /// kurarsa kapı yine atlanabilir — o yüzden F4'ün girdisi bu tip olmalı ve
    /// bu cümle ticket'ta da duruyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kapiyi_atlayan_ikinci_yol_yok()
    {
        // Yapıcıların HEPSİ private — `internal` bir yapıcı derleme içinden
        // ikinci bir kapı açardı ve dışarıdan bakan hiçbir test bunu görmezdi.
        var yapicilar = typeof(RedactedPrompt)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.All(yapicilar, c => Assert.True(c.IsPrivate, $"Yapıcı private değil: {c}"));
        Assert.NotEmpty(yapicilar);

        var ureticiler = typeof(RedactedPrompt).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => Donen(m) == typeof(RedactedPrompt))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["RedactedPrompt.Redact"], ureticiler);
    }

    /// <summary>
    /// Yapıcılar bilinçli olarak dışarıda: onları yukarıdaki <c>IsPrivate</c>
    /// iddiası tutuyor. Burada aranan şey <b>bir örneği elden çıkaran</b> ikinci
    /// bir üye — bir fabrika metodu, bir önbellek alanı, bir özellik.
    /// </summary>
    private static Type? Donen(MemberInfo member) => member switch
    {
        MethodInfo method => method.ReturnType,
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => null,
    };

    // ------------------------------------------------------- kriter 6

    /// <summary>
    /// Kriter 6: desen tek yerde.
    ///
    /// <para>
    /// <c>ConfigNormalizer</c> anahtar kelime listesinin kendi kopyasını
    /// taşımıyor; <see cref="SecretPatterns"/>'tan okuyor. İki liste
    /// doğsaydı biri sertleştiğinde diğeri eski hâliyle kalırdı ve
    /// <b>ayrışma sessiz olurdu</b>.
    /// </para>
    ///
    /// <para>
    /// Taşımanın davranış sınavı burada değil: onu
    /// <c>SimulatorFixtureChainTests</c> tutuyor ve taşımadan sonra aynı sonucu
    /// vermeli.
    /// </para>
    /// </summary>
    [Fact]
    public void Anahtar_kelime_listesi_tek_yerde()
    {
        var kopya = typeof(ConfigNormalizer).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributesData())
            .Where(a => a.AttributeType.Name == "GeneratedRegexAttribute")
            .Select(a => a.ConstructorArguments[0].Value as string ?? string.Empty)
            .Where(p => p.Contains("psksecret", StringComparison.OrdinalIgnoreCase)
                || p.Contains("pre-shared-key", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            kopya.Length == 0,
            "Bizigo.Devices hâlâ kendi sır deseninin kopyasını taşıyor:\n  " + string.Join("\n  ", kopya));
    }

    /// <summary>
    /// Ortak liste gerçekten <b>iki taraftan da</b> okunuyor: aynı anahtar
    /// kelime config satırında ve log satırında birden tutuyor.
    /// </summary>
    [Fact]
    public void Ayni_liste_iki_capayla_okunuyor()
    {
        const string deger = "SAHTE-Ux4vY7zA1bC5dE8f";

        var config = ConfigNormalizer.Normalize(
            ConfigNormalizer.CiscoAsa,
            $"tunnel-group 203.0.113.9 ipsec-attributes\n ikev2 remote-authentication pre-shared-key {deger}\n");

        Assert.DoesNotContain(deger, string.Join("\n", config.Select(l => l.Section + l.Text)), StringComparison.Ordinal);

        var log = RedactedPrompt.Redact(
            $"Sep  3 11:06:41 asa-dc-01 : %ASA-5-111008: User 'x' executed the 'ikev2 remote-authentication pre-shared-key {deger}'\n");

        Assert.DoesNotContain(deger, log.Text, StringComparison.Ordinal);

        // Ve config deseni log satırında TEK BAŞINA çalışmıyor — çapa farkı
        // gerçek. Bu iddia düşerse `LogAssignment` gereksiz demektir.
        Assert.False(
            SecretPatterns.ConfigAssignment().IsMatch(
                $"Sep  3 11:06:41 asa-dc-01 : %ASA-5-111008: User 'x' executed the 'ikev2 remote-authentication pre-shared-key {deger}'"),
            "Config deseni syslog satırında eşleşti — çapa ayrımının gerekçesi düştü, §3'ün tuzağı yeniden okunmalı.");
    }

    // ------------------------------------------------------- kriter 11

    /// <summary>
    /// Kriter 11: <c>MinFragment = 6</c> eşiği prompt bağlamında <b>geçerli
    /// değil</b> ve karar gerekçesiyle burada sınanıyor.
    ///
    /// <para>
    /// Eşiğin gerekçesi <i>türetilmiş</i> parçalarla ilgili: bir webhook
    /// URL'inden çıkan <c>api</c> parçası sır değil gürültü. Keşif yolunda
    /// türetme yok — değer, bir sır atamasının sağ tarafı olarak bulundu.
    /// ASA'nın dört karakterlik bir SNMP community'si gerçek bir sır ve altı
    /// karakterlik bir taban onu sessizce atlardı.
    /// </para>
    ///
    /// <para>
    /// Eşik <b>korunuyor</b> — ama yalnızca bilinen-sır yolunda. İki yol iki
    /// soru soruyor ve tek eşik ikisine birden cevap veremiyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kisa_sir_kesif_yolunda_maskeleniyor_bilinen_yolda_esik_duruyor()
    {
        var kisa = RedactedPrompt.Redact(
            "Sep  3 11:09:03 asa-dc-01 : %ASA-5-111008: User 'x' executed the 'snmp-server community pub1'\n");

        Assert.DoesNotContain("pub1", kisa.Text, StringComparison.Ordinal);

        // Bilinen-sır yolu değişmedi: altı karakterin altındaki TÜREV hâlâ
        // maskelenmiyor, çünkü orada soru başka.
        Assert.Equal(
            "https://a.example/v1 çağrısı düştü",
            SecretRedactor.Redact("https://a.example/v1 çağrısı düştü", "v1"));
    }

    // ------------------------------------------------------- değer sınırı

    /// <summary>
    /// Boşluk ayırıcının <b>iki yönü tek testte</b>: vendor söz dizimindeki
    /// anahtar maskeleniyor, düz anlatımdaki sözcük maskelenmiyor.
    ///
    /// <para>
    /// İkisini ayrı testlere koymak kuralı ikiye bölerdi ve biri
    /// değiştirildiğinde diğerinin hâlâ geçerli olup olmadığı görünmezdi.
    /// Kural tek: <b>boşluk ayırıcıda değer ilk belirteçtir</b>, ve o belirteç
    /// düz bir sözcükse değer değildir.
    /// </para>
    ///
    /// <para>
    /// İlk hâl satır sonuna kadar maskeliyordu; altın korpusta 0 kez oluyordu
    /// ama <b>sayı 0 olduğu için değil katalogda sshd olmadığı için</b>.
    /// Öngörülen bir kaybı sayaca havale etmek sayacın işini yapmıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Bosluk_ayiricida_deger_ilk_belirtec_ve_duz_sozcuk_deger_degil()
    {
        const string duz = "Sep  3 11:14:02 srv01 sshd[2211]: Failed password for admin from 10.1.2.3 port 51022 ssh2";

        var anlatim = RedactedPrompt.Redact(duz + "\n");

        Assert.Equal(duz + "\n", anlatim.Text);
        Assert.Equal(0, anlatim.MaskedValues);

        var vendor = RedactedPrompt.Redact(
            "Sep  3 11:09:03 asa-dc-01 : %ASA-5-111008: User 'x' executed the 'snmp-server community S3cret' command.\n");

        Assert.DoesNotContain("S3cret", vendor.Text, StringComparison.Ordinal);

        // Ve YALNIZCA ilk belirteç: satırın geri kalanı RCA'nın bağlamı.
        Assert.Contains("command.", vendor.Text, StringComparison.Ordinal);

        // `=`/`:` tarafında kural değişmiyor — orada sınır gerçekten belirsiz.
        var atama = RedactedPrompt.Redact("… user = svc-mon : password = S3cret rest of line\n");

        Assert.DoesNotContain("rest of line", atama.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Şifreleme işareti değerin kendisi değil: <c>ENC</c> maskelenirse sır
    /// olduğu gibi kalır ve kapı "bir şey maskeledim" der.
    /// </summary>
    [Fact]
    public void Sifreleme_isareti_deger_sanilmiyor()
    {
        var sonuc = RedactedPrompt.Redact(
            "Sep  3 11:20:11 fw-ankara-01 cfg: User 'x' executed the 'set psksecret ENC Kx2mVb9nR4tL' command.\n");

        Assert.DoesNotContain("Kx2mVb9nR4tL", sonuc.Text, StringComparison.Ordinal);
        Assert.Contains("ENC", sonuc.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- katman B

    /// <summary>
    /// Katman B'nin <b>içeride kalan</b> yarısı: kendini tarif eden ve
    /// eskimeyen biçimler. Sağlayıcı anahtar katalogları bilinçli kapsam dışı —
    /// bu test onların maskelenmediğini de yazıyor, çünkü "kapsam dışı" bir
    /// karar, bir eksiklik değil.
    /// </summary>
    [Fact]
    public void B_dar_kalıyor_saglayici_kataloglari_kapsam_disinda()
    {
        var saglayici = RedactedPrompt.Redact("audit: key AKIAIOSFODNN7EXAMPLE rotated by svc-mon\n");

        Assert.Contains("AKIAIOSFODNN7EXAMPLE", saglayici.Text, StringComparison.Ordinal);
        Assert.Equal(0, saglayici.MaskedValues);
    }
}

using Bizigo.Contracts;
using Bizigo.Rca;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Ajan tetiklemesinin anahtarı — modelin kotayı yiyememesi.</b>
///
/// <para>
/// Planın şartı <c>rca.trigger</c>'ın bir <c>Idempotency-Key</c> taşıması;
/// koordinatörün çividiği kısıt anahtarın <b>modelin uydurabileceği bir yerden
/// gelmemesi</b>. Bu dosya ikisinin birlikte ne anlama geldiğini ölçüyor.
/// </para>
///
/// <para>
/// <b>Neden bu testler var — engellenen şey somut.</b> Anahtar bir araç
/// argümanı olsaydı idempotency'nin kendisi modele bırakılmış olurdu: her
/// denemede yeni anahtar üreten model aynı RCA için <b>kotayı defalarca</b>
/// tüketir, aynı anahtarı ısrarla üreten model farklı bir tetiklemeyi
/// <b>yanlışlıkla bastırır</b>. İkisi de sessiz.
/// </para>
/// </summary>
public sealed class McpIdempotencyTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <b>Aynı çağrı aynı anahtarı üretiyor</b> — modelin tekrar denemesi tek
    /// RCA'ya iniyor.
    /// </summary>
    [Fact]
    public void Ayni_cagri_ayni_anahtari_uretiyor() =>
        Assert.Equal(
            McpIdempotency.KeyFor("ayse", ["core"], From, To),
            McpIdempotency.KeyFor("ayse", ["core"], From, To));

    /// <summary>
    /// <b>Farklı pencere farklı anahtar</b> — meşru bir yeniden tetikleme
    /// bastırılmıyor.
    ///
    /// <para>
    /// Bu, kalıcı stabil bir anahtarın (yalnızca özne + kapsam) neden
    /// elendiğinin ölçümü: <c>RcaAdmission</c>'ın idempotency araması
    /// <b>zaman sınırsız</b>, yani bir anahtar bir kez kullanıldıysa sonsuza
    /// kadar aynı koşumu döndürüyor. Pencerenin anahtara girmesi o tuzağı
    /// kapatıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Farkli_pencere_farkli_anahtar() =>
        Assert.NotEqual(
            McpIdempotency.KeyFor("ayse", ["core"], From, To),
            McpIdempotency.KeyFor("ayse", ["core"], From, To.AddMinutes(1)));

    /// <summary>Farklı kullanıcı farklı anahtar — biri diğerini bastıramıyor.</summary>
    [Fact]
    public void Farkli_ozne_farkli_anahtar() =>
        Assert.NotEqual(
            McpIdempotency.KeyFor("ayse", ["core"], From, To),
            McpIdempotency.KeyFor("mehmet", ["core"], From, To));

    /// <summary>Farklı kapsam farklı anahtar.</summary>
    [Fact]
    public void Farkli_kapsam_farkli_anahtar() =>
        Assert.NotEqual(
            McpIdempotency.KeyFor("ayse", ["core"], From, To),
            McpIdempotency.KeyFor("ayse", ["guvenlik"], From, To));

    /// <summary>
    /// <b>Kapsamın sırası ve tekrarı anahtarı değiştirmiyor.</b>
    ///
    /// <para>
    /// Aynı kapsamı farklı sırayla yazan bir model, aksi hâlde <b>yeni bir
    /// anahtar</b> üretip kotadan ikinci kez yerdi — ve bunu fark eden hiçbir
    /// şey olmazdı. Kanonikleştirme <c>RcaTriggerKey.Scope</c>'ta ve ikinci bir
    /// kopyası yok (§9).
    /// </para>
    /// </summary>
    [Fact]
    public void Kapsam_sirasi_ve_tekrari_anahtari_degistirmiyor() =>
        Assert.Equal(
            McpIdempotency.KeyFor("ayse", ["core", "guvenlik"], From, To),
            McpIdempotency.KeyFor("ayse", ["guvenlik", "core", "core"], From, To));

    /// <summary>
    /// <b>Hash'e giren küme DAR ve bu testin varlık sebebi o.</b>
    ///
    /// <para>
    /// Anahtar yalnızca <b>kimlik taşıyan</b> alanlardan türüyor: çağıran,
    /// kapsam, pencere. Argüman torbasının tamamı girseydi model anlamsız bir
    /// alan ekleyip anahtarı değiştirebilirdi — kotayı yeme yolunun ta kendisi.
    /// </para>
    ///
    /// <para>
    /// Bu test bir <b>imza</b> tutuyor, bir davranış değil: <c>KeyFor</c>'a yeni
    /// bir parametre eklemek burayı kırıyor, yani o alanın anahtara girip
    /// girmeyeceği <b>bilinçli bir karar</b> oluyor. Varsayılan olarak
    /// girmesi, sessizce yanlış olan hâl.
    /// </para>
    /// </summary>
    [Fact]
    public void Anahtarin_girdileri_yazili_kalıyor()
    {
        var parameters = typeof(McpIdempotency)
            .GetMethod(nameof(McpIdempotency.KeyFor))!
            .GetParameters()
            .Select(static p => p.Name!)
            .ToArray();

        Assert.Equal<string>(["subject", "ownerGroups", "from", "to"], parameters);
    }

    /// <summary>
    /// <b>Anahtar kaynağını taşıyor.</b> <c>rca_runs</c>'a bakan biri anahtarın
    /// nereden geldiğini görebilmeli.
    /// </summary>
    [Fact]
    public void Anahtar_kaynagini_soyluyor() =>
        Assert.StartsWith(
            $"{McpIdempotency.Prefix}:ayse:",
            McpIdempotency.KeyFor("ayse", ["core"], From, To),
            StringComparison.Ordinal);

    /// <summary>
    /// <b>Ajan tetiklemesi <c>External</c> değil</b> — ve bu ayrım M05'in
    /// taşıyıcı kararı.
    ///
    /// <para>
    /// Bu üründe <c>Idempotency-Key</c> kaynağı <b>belirleyen</b> şey:
    /// <c>POST /v1/rca</c> anahtarlı talebi dış API, anahtarsızı kullanıcı
    /// sayıyor. MCP ikisine de uymuyor — bir dış sistem değil, kullanıcının
    /// ajanı. <c>External</c> demek <c>rca.runs</c>'ta <b>yanlış bir cevap</b>
    /// göstermek olurdu: alan dolu, değer makul, anlam yanlış (§7).
    /// </para>
    /// </summary>
    [Fact]
    public void Ajan_tetiklemesi_kendi_kaynagini_tasiyor()
    {
        var request = RcaTriggerSources.FromAgent(
            "ayse", McpIdempotency.KeyFor("ayse", ["core"], From, To), ["core"], From, To);

        Assert.Equal(RcaTriggerSource.Agent, request.Source);
        Assert.NotEqual(RcaTriggerSource.External, request.Source);
        Assert.NotEqual(RcaTriggerSource.Manual, request.Source);

        // Anahtarı TAŞIYOR: `Manual` gibi anahtarsız olsaydı modelin tekrar
        // denemesi ikinci bir koşum açardı.
        Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey));
    }

    /// <summary>
    /// <b>Ajan kota rezervinden etkilenmiyor</b> — ölçüldü, varsayılmadı.
    ///
    /// <para>
    /// <c>RcaQuotaGate.EffectiveLimit</c> rezervi yalnızca <c>Schedule</c> için
    /// ayırıyor. <i>"Neden ajan rezervden yemiyor"</i> sorusu bir gün sorulacak
    /// ve cevabı bir varsayım değil bu satır olmalı.
    /// </para>
    /// </summary>
    [Fact]
    public void Ajan_kota_rezervinden_etkilenmiyor()
    {
        const int daily = 100;
        const int reserve = 20;

        Assert.Equal(daily, RcaQuotaGate.EffectiveLimit(daily, reserve, RcaTriggerSource.Agent));

        // Ve rezerv gerçekten BİR ŞEY yapıyor: `Schedule` için düşüyor. Yoksa
        // yukarıdaki eşitlik "rezerv hiç çalışmıyor" hâliyle de yeşil kalırdı.
        Assert.Equal(daily - reserve, RcaQuotaGate.EffectiveLimit(daily, reserve, RcaTriggerSource.Schedule));
    }
}

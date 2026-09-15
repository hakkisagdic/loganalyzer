using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M20 · Abonelik bildiriminin YAN KANALI — büyüklüğü ölçülüyor.</b>
///
/// <para>
/// M07 iki sınırı yazılı bıraktı ve ikincisi şuydu: <i>"bildirim kapsam
/// süzgecinden geçmiyor; içerik taşımadığı için veri sızmıyor ama zamanlaması
/// bir sinyal."</i> O cümlenin eksik yarısı <b>ne kadar</b> sinyal olduğu —
/// ve M20'nin ilk işi bunu ölçmek, çünkü <i>"ihmal edilebilir"</i> demek bir
/// ölçüm değil bir tahmin.
/// </para>
///
/// <h3>Ölçülen şey</h3>
///
/// <para>
/// Bildirim <b>yalnızca adres</b> taşıyor (<c>bizigo://rca-runs</c>) ve o adresi
/// okumak kapsam kapısından geçiyor. Yani abone kapsamı dışındaki bir koşumun
/// <b>hiçbir alanını</b> göremiyor. Sızan şey bildirimin <b>varlığı</b> ve
/// <b>anı</b>.
/// </para>
///
/// <para>
/// Bu testler o kanalın kapasitesini <b>sayıyla</b> veriyor: kaç bildirim, kaç
/// koşuma karşılık, ve abonenin bundan çıkarabildiği şey ne.
/// </para>
/// </summary>
public sealed class McpSubscriptionSideChannelTests
{
    /// <summary>
    /// <b>Yan kanal ihmal edilebilir DEĞİL: yabancı koşumların TAM SAYISI sızıyor.</b>
    ///
    /// <para>
    /// Ölçüm: kapsamı yalnızca <c>network/core</c>'u gören bir aboneye,
    /// <c>network/edge</c>'de koşan <b>üç</b> RCA koşumu boyunca kaç bildirim
    /// gidiyor. Sonuç <b>koşum başına üç</b>, yani abone bildirimleri sayıp
    /// üçe bölerek <b>göremediği gruptaki koşum sayısını</b> tam olarak
    /// buluyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bilgi miktarı:</b> bu bir kesirli bit değil, <b>tam kardinalite</b>.
    /// Bir pencerede yabancı koşum sayısı 0..N arasındaysa abone o sayıyı
    /// <b>kesin</b> öğreniyor — yani ~log₂(N+1) bit, artı her geçişin
    /// <b>zaman damgası</b> (bildirimin geldiği an, milisaniye çözünürlüğünde).
    /// </para>
    ///
    /// <para>
    /// <b>Neden bu bir karar:</b> bir RCA koşumunun <i>varlığı</i> operasyonel
    /// bir olgu — o pencerede o ekipte bir alarm tetiklendi demek. Sayısı ve
    /// zamanlaması bir olay grafiği çiziyor: hangi ekip ne sıklıkta alarm
    /// alıyor, bir olay ne zaman başladı, ne kadar sürdü. K17 kapsamı
    /// <i>satırları</i> korumak için var, ama bu grafik satır okumadan
    /// çıkarılıyor.
    /// </para>
    ///
    /// <para>
    /// Yani süzgeç <b>yazılmalı</b>, ve gerekçesi bu sayı: kapasite
    /// <i>sıfıra yakın</i> değil, <b>koşum başına üç bildirim</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yabanci_grubun_kosum_sayisi_bildirimlerden_cikarilabiliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        var sayac = new SayanDinleyici();
        var factory = new InMemoryControlPlaneFactory();

        var admission = new RcaAdmission(
            factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            TimeProvider.System,
            [sayac]);

        const int YabanciKosum = 3;

        for (var i = 0; i < YabanciKosum; i++)
        {
            var kabul = await admission.AdmitAsync(
                Talep($"yabanci-{i}", "network/edge"),
                ct);

            await admission.TryStartAsync(kabul.Run.Id, RcaModelBoundaryStamp.NotEngaged(), ct);
            await admission.StopAsync(kabul.Run.Id, RcaStopReason.OperatorCancelled, "ölçüm", ct);
        }

        // KOŞUM BAŞINA ÜÇ BİLDİRİM. Abone bunları sayıp üçe bölerek göremediği
        // gruptaki koşum sayısını TAM olarak buluyor.
        Assert.Equal(YabanciKosum * 3, sayac.Sayi);

        // VE HİÇBİR SATIR OKUYAMIYOR — kapsam kapısı yerinde. Sızan şey içerik
        // değil KARDİNALİTE. İki iddia bir arada olmak zorunda: yalnızca
        // birincisi "kapı yok" gibi, yalnızca ikincisi "sorun yok" gibi okunur.
        await using var db = factory.CreateDbContext();

        var gorunen = await db.RcaRuns
            .AsNoTracking()
            .Where(r => r.OwnerGroup == "network/core")
            .CountAsync(ct);

        Assert.Equal(0, gorunen);
    }

    /// <summary>
    /// <b>Kanalın kapasitesi kapsam süzgeciyle SIFIRA iniyor.</b>
    ///
    /// <para>
    /// Süzgecin kararı saf bir fonksiyon (<c>McpSubscriptionRegistry.Delivers</c>)
    /// ve burada <b>doğrudan</b> ölçülüyor. Kararı akışın içinde ölçmek, iki
    /// şeyi birbirine karıştırırdı: süzgecin doğruluğu ile taşımanın çalışması.
    /// </para>
    ///
    /// <para>
    /// Üç hâl ve üçü de ayrı bir karar:
    /// </para>
    /// <list type="bullet">
    /// <item>Kendi grubu → <b>gidiyor</b> (aksi hâlde abonelik işe yaramaz).</item>
    /// <item>Yabancı grup → <b>gitmiyor</b> (yan kanal kapanıyor).</item>
    /// <item>Sınırsız kapsam → <b>gidiyor</b> (operatör her şeyi görüyor; kapsam
    /// çözücüsünün bilinçli kararı, burada ikinci kez verilmiyor).</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("network/core", true)]
    [InlineData("network/edge", false)]
    public void Suzgec_yabanci_grubu_elemekle_kanali_kapatiyor(string ownerGroup, bool beklenen) =>
        Assert.Equal(
            beklenen,
            Bizigo.Mcp.McpSubscriptionRegistry.Delivers(
                AccessScope.ForGroups("abone", ["network/core"]),
                ownerGroup));

    /// <summary>Sınırsız kapsam her grubu görüyor — çözücünün kararı taşınıyor.</summary>
    [Fact]
    public void Sinirsiz_kapsam_her_grubu_goruyor() =>
        Assert.True(
            Bizigo.Mcp.McpSubscriptionRegistry.Delivers(
                AccessScope.System("yonetici"),
                "network/edge"));

    /// <summary>
    /// <b>Grubu bilinmeyen bir değişiklik GİTMİYOR.</b>
    ///
    /// <para>
    /// Boş bir <c>owner_group</c> "her gruba ait" değil "bilinmiyor" demek, ve
    /// bilinmeyeni göndermek yan kanalı geri açardı. Varsayılanın <b>kapalı</b>
    /// olması <see cref="McpSurface.Unspecified"/> kalıbının aynısı: beyansız
    /// bir değer bir varsayılan değil bir rettir.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Grubu_bilinmeyen_degisiklik_gonderilmiyor(string ownerGroup) =>
        Assert.False(
            Bizigo.Mcp.McpSubscriptionRegistry.Delivers(
                AccessScope.ForGroups("abone", ["network/core"]),
                ownerGroup));

    private static RcaTriggerRequest Talep(string identity, string ownerGroup) => new()
    {
        Source = RcaTriggerSource.Manual,
        Identity = identity,
        OwnerGroup = ownerGroup,
        WindowFrom = DateTimeOffset.UnixEpoch,
        WindowTo = DateTimeOffset.UnixEpoch.AddMinutes(45),
    };

    private sealed class SayanDinleyici : IRcaRunChangeListener
    {
        public int Sayi { get; private set; }

        public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken)
        {
            Sayi++;

            return ValueTask.CompletedTask;
        }
    }
}

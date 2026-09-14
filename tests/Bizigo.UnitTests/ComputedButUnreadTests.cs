using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Bizigo.Cli;
using Bizigo.Contracts;
using Bizigo.Rca;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>HESAPLANIP ATILAN alan kalmasın</b> — M15'in bulduğu kusurun mekanik hâli
/// (M16).
///
/// <h3>Kusurun şekli</h3>
///
/// <para>
/// <see cref="RcaQuotaUsage.BySource"/> her koşumda <b>doluyor</b> ve T46 onu
/// bilerek yazdı: <i>"§6.1'in riskini görünür kılan tek şey bu sayaç."</i> Ama
/// M15'te ölçüldü ki üretimde <b>hiç kimse okumuyordu</b> —
/// <c>UsageAsync</c>'in tek çağıranı kendi <c>CheckAsync</c>'i ve kırılımı
/// okuyan tek yer bir birim testiydi. Yani sayaç <b>hesaplanıyor ve atılıyor</b>;
/// <c>EventReservePercent</c>'in belgesi <i>"operatör veriden görebiliyor"</i>
/// diyordu ve o cümle yanlıştı.
/// </para>
///
/// <para>
/// <b>Bu, bu deponun bir sınıfı ve adı yoktu:</b> bir sayının hesaplanması,
/// sorulabilir olması demek değil. Aynı şekil <c>Produces&lt;T&gt;</c>
/// kapısında ödendi — kolonlar yerindeydi, kapı üç uç dosyasını hiç görmedi.
/// </para>
///
/// <h3>Neden MemberRef tablosu, IL çağrı grafiği değil</h3>
///
/// <para>
/// Soru <b>dar</b>: <i>bu üyeye üretimde dokunan bir derleme var mı.</i>
/// <c>IlCallReader</c>'ın gövde yürüyüşü bir kökten başlayıp
/// <b>erişilebilir</b> çağrıları geziyor ve o soru için fazla — ayrıca hangi
/// köke bakılacağını seçmek gerekirdi. Meta veri tablosu erişilebilirliğe
/// bakmıyor: derleyici bir üyeyi okuyan her <c>call</c> için oraya satır
/// yazıyor. Kalıp <c>McpProductScopeGateTests</c>'ten.
/// </para>
///
/// <para>
/// <b>Ve bu soru ancak DIŞ derlemeden sorulabiliyor.</b>
/// <see cref="RcaQuotaUsage"/> <c>Bizigo.Rca</c>'da tanımlı, yani o derlemenin
/// kendi içindeki bir okuma <c>MethodDef</c> olur ve MemberRef tablosunda
/// <b>görünmez</b>. Bekçi bu yüzden <c>Bizigo.Cli</c>'ye bakıyor: okuyucunun
/// yaşadığı yer orası ve orada <c>MemberRef</c> olarak görünüyor.
/// </para>
///
/// <h3>⚠️ Bu bekçinin GÖREMEDİKLERİ — beyan</h3>
///
/// <list type="number">
/// <item>
/// <b>Genel bir "hesaplanıp atılan alan" kuralı DEĞİL.</b> Bekçi <b>bu</b> alanı
/// adıyla sınıyor. Genel hâli yazılamadı ve sebebi ölçülebilir: bir tipin her
/// özelliği için "üretimde okunuyor mu" sormak, meşru olarak okunmayan
/// onlarca alanı da kırmızı yakardı — bir <c>record</c>'un eşitliğe giren ama
/// hiç okunmayan alanı, serileştirilip tele inen ama kod tarafından
/// okunmayan alanı. Ayrımı yapabilen mekanik bir ölçüt <b>yok</b>: hangi
/// alanın bir <i>karar</i> için var olduğunu bilen tek şey onu yazan kişi.
/// </item>
/// <item>
/// <b>Okumanın ANLAMLI olduğunu ölçmüyor — ve bu ÖLÇÜLDÜ.</b> §6 ölçümünde
/// raporun kırılımı hiç okumadığı bir kusur uygulandı
/// (<c>used = 0</c>) ve bu bekçi <b>YEŞİL KALDI</b>: rapor
/// <c>BySource</c>'a başka bir yerden de dokunuyor
/// (<c>usage.BySource.Count == 0</c>), yani tablo satırı duruyordu. Bir çağrının
/// varlığı, o çağrının işe yaradığını söylemiyor.
/// </item>
/// <item>
/// Anlamı tutan şey bu yüzden ayrı bir bekçi
/// (<see cref="Rapor_kaynak_kirilimini_ve_etkin_siniri_basiyor"/>) — <b>ve o da
/// aynı ölçümde bir kez yeşil kaldı:</b> ilk hâli yalnızca kaynak adlarını ve
/// etkin sınırları sınıyordu, ikisi de kırılımı <b>okumadan</b> üretilebilir.
/// Şimdi <c>BySource</c>'tan gelen <b>sayıları</b> çiviliyor. İkisi birlikte
/// dersi taşıyor: <i>bir alanın okunduğunu ölçmek, okunanın kullanıldığını
/// ölçmek değil.</i>
/// </item>
/// <item>
/// <b>Yansımayla okuma görünmüyor</b> — MemberRef'e girmiyor.
/// </item>
/// </list>
///
/// <para>
/// Yani bu bekçi bir <b>sınıfın</b> ilk örneği, sınıfın kendisi değil. Genel
/// kuralın yazılamamasının sebebi yukarıda; bir sonraki kişi onu aramasın.
/// </para>
/// </summary>
public sealed class ComputedButUnreadTests
{
    /// <summary>
    /// <b><see cref="RcaQuotaUsage.BySource"/>'un ÜRETİMDE bir okuyucusu var.</b>
    ///
    /// <para>
    /// M15'ten önce bu test <b>kırmızı yanardı</b> — kırılımı okuyan tek yer bir
    /// birim testiydi, ve bir birim testi bir okuyucu değil: ölçüm oraya
    /// bakmayan hiç kimseye ulaşmıyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kaynak_kirilimini_uretimde_bir_okuyan_var()
    {
        // Kapının kendi ön şartı: tip DIŞ bir derlemede olmalı, yoksa okuma
        // `MethodDef` olur ve tabloya hiç girmez — bekçi sessizce yeşil yanar.
        Assert.NotEqual(
            typeof(RcaQuotaCommandHandlers).Assembly,
            typeof(RcaQuotaUsage).Assembly);

        var referenced = MemberReferences(typeof(RcaQuotaCommandHandlers).Assembly);

        Assert.Contains($"{nameof(RcaQuotaUsage)}.get_{nameof(RcaQuotaUsage.BySource)}", referenced);

        // KARŞI-KANIT: tablo gerçekten dolu ve okunabiliyor. Boş gelseydi
        // yukarıdaki iddia hiçbir şey ifade etmezdi (§6).
        Assert.NotEmpty(referenced);
    }

    /// <summary>
    /// <b>Rapor kırılımı GERÇEKTEN basıyor</b> — ve etkin sınırı kaynak başına.
    ///
    /// <para>
    /// Yukarıdaki bekçi bir <i>çağrının varlığını</i> ölçüyor; bu test
    /// <b>çıktıyı</b>. İkisi ayrı, çünkü ayrı kaybediliyor: alanı okuyup
    /// sonucu atan bir kod MemberRef tablosunda aynı satırı üretir.
    /// </para>
    ///
    /// <para>
    /// <c>Format</c> saf olduğu için veritabanı gerekmiyor — konteynersiz
    /// ölçülebiliyor (§2).
    /// </para>
    /// </summary>
    [Fact]
    public void Rapor_kaynak_kirilimini_ve_etkin_siniri_basiyor()
    {
        var options = new RcaQuotaOptions { DailyPerGroup = 4, EventReservePercent = 50 };

        var usage = new RcaQuotaUsage(
            "network/core",
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            Used: 3,
            Limit: 4,
            BySource: new Dictionary<RcaTriggerSource, int>
            {
                [RcaTriggerSource.Agent] = 2,
                [RcaTriggerSource.Alert] = 1,
            });

        var report = RcaQuotaCommandHandlers.Format(usage, options);

        // KIRILIM BASILIYOR: iki kaynak ve sayıları.
        Assert.Contains("agent", report, StringComparison.Ordinal);
        Assert.Contains("alert", report, StringComparison.Ordinal);

        // TÜKETİMİ OLMAYAN kaynak da basılıyor — "hiç koşmamış" ile "raporda
        // yok" aynı şeye inmesin. `BySource` yalnızca koşmuş kaynakları taşıyor.
        Assert.Contains("schedule", report, StringComparison.Ordinal);
        Assert.Contains("manual", report, StringComparison.Ordinal);

        var agentLine = report.Split('\n').Single(l => l.Contains("agent", StringComparison.Ordinal));
        var alertLine = report.Split('\n').Single(l => l.Contains("alert", StringComparison.Ordinal));
        var scheduleLine = report.Split('\n').Single(l => l.Contains("schedule", StringComparison.Ordinal));

        // ⚠️ TÜKETİM SAYILARI — ve bu iddia bir kırmızı ölçümünden doğdu.
        //
        // İlk hâli yalnızca kaynak ADLARINI ve ETKİN SINIRLARI sınıyordu, ve §6
        // ölçümü onu YAKALADI: raporun `BySource`'u hiç okumadığı bir kusurda
        // (`used = 0`) test YEŞİL KALDI. Sebebi şu — adlar enum'dan geliyor,
        // sınırlar `EffectiveLimit`'ten; ikisi de kırılımı OKUMADAN üretilebilir.
        // Yani test raporun şeklini ölçüyordu, TAŞIDIĞI VERİYİ değil.
        //
        // Bu satırlar `BySource`'tan gelen sayıları çiviliyor: kırılım
        // okunmazsa ajan 2 yerine 0 basar ve burası kırmızı yanar.
        Assert.Contains(" 2 /", agentLine, StringComparison.Ordinal);
        Assert.Contains(" 1 /", alertLine, StringComparison.Ordinal);

        // Ve tüketimi OLMAYAN kaynak sıfır basıyor — `BySource`'ta yok, ama
        // raporda var. "Yok" ile "sıfır" ayrımının rapor tarafındaki hâli.
        Assert.Contains(" 0 /", scheduleLine, StringComparison.Ordinal);

        // ETKİN SINIR KAYNAK BAŞINA: rezerv açıkken alarm 4, ajan 2 görüyor.
        // Tek bir "limit" satırı rezervin varlığını görünmez kılardı — yani
        // M15'in düzelttiği şeyi rapor katmanında geri getirirdi.
        Assert.Contains("/ 2", agentLine, StringComparison.Ordinal);
        Assert.Contains("/ 4", alertLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>0</c> "sınırsız" diye basılıyor, "0" diye değil.</b>
    ///
    /// <para>
    /// İkisini aynı basmak raporun yapabileceği en pahalı hata: <i>"tavan
    /// sıfır"</i> ile <i>"tavan yok"</i> zıt iş emri veriyor. Aynı ayrım
    /// <c>RcaQuotaUsage.Remaining</c>'de de var ve aynı sebeple.
    /// </para>
    /// </summary>
    [Fact]
    public void Sinirsiz_tavan_sifir_diye_basilmiyor()
    {
        var report = RcaQuotaCommandHandlers.Format(
            new RcaQuotaUsage("network/core", DateTimeOffset.UnixEpoch, 0, 0, new Dictionary<RcaTriggerSource, int>()),
            new RcaQuotaOptions { DailyPerGroup = 0 });

        Assert.Contains("sınırsız", report, StringComparison.Ordinal);

        // Ve boş pencere kendini söylüyor: "veri birikmedi" ile "kota dolu"
        // aynı rapora düşerse operatör yanlış karar verir.
        Assert.Contains("veri henüz birikmedi", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Derlemenin <c>MemberRef</c> tablosundaki <c>Tip.üye</c> adları.
    /// </summary>
    /// <remarks>
    /// Kalıp <c>McpProductScopeGateTests</c>'ten; ikinci bir okuyucu yazmak
    /// yerine aynı tekniği kullanıyor. Ortak bir yardımcıya çıkarılmadı çünkü
    /// iki bekçi <b>farklı</b> derlemelere bakıyor ve ortaklaştırma bir
    /// parametreden fazlasını kazandırmıyor.
    /// </remarks>
    private static IReadOnlyList<string> MemberReferences(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var reader = new PEReader(stream);

        var metadata = reader.GetMetadataReader();
        var names = new List<string>();

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            var name = metadata.GetString(member.Name);

            var parent = member.Parent.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(
                    metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name),
                _ => string.Empty,
            };

            names.Add($"{parent}.{name}");
        }

        return names;
    }
}

using Bizigo.ControlPlane;
using Bizigo.Evidence;

using Microsoft.EntityFrameworkCore;

namespace Bizigo.Rca.Reasoning;

/// <summary>
/// Saklanmış bir rapor: belge <b>artı</b> kaydın kendi kimliği ve zamanı.
///
/// <para>
/// Ayrı bir tip, çünkü <see cref="RcaReportDocument"/> <b>üretilen belgenin</b>
/// sahibi — kaydın kimliği ve yazıldığı an belgenin değil <b>satırın</b>
/// özellikleri. Belgeye gömülselerdi, aynı belgeyi iki kez saklamak iki farklı
/// belge üretirdi ve "aynı girdi, aynı çıktı" iddiası tanım gereği yanlış
/// olurdu — <c>EvidenceBundle.ContentHash</c>'in duvar saatini dışarıda tutma
/// sebebinin aynısı.
/// </para>
/// </summary>
public sealed record StoredRcaReport(Guid Id, DateTimeOffset CreatedAt, RcaReportDocument Document);

/// <summary>
/// Üretilen RCA belgelerinin kalıcı deposu (T51).
///
/// <para>
/// <b>Tek yazan taraf.</b> Varlıktaki sorgulanabilir sayılar
/// (<c>produced/dropped/fabricated</c>) belgenin içindekinin kopyası;
/// <see cref="ToEntity"/> ikisini de aynı belgeye bakarak dolduruyor ve bir
/// test eşitliği tutuyor. İki ayrı yerden yazmak, bir gün ayrışmalarının ve
/// bunun hiçbir yerde görünmemesinin en kolay yolu olurdu —
/// <c>EvidenceBundleStore</c>'da aynı karar aynı gerekçeyle verildi.
/// </para>
///
/// <h3>Kapsam kapısı bir tip, bir alışkanlık değil</h3>
///
/// <para>
/// Okuma metotları <see cref="Guid"/> değil <see cref="EvidenceBundle"/>
/// istiyor. Sebebi K17: <b>raporun kendi <c>owner_group</c>'u yok</b>, kapsamını
/// üretildiği paketten devralıyor. <c>Guid</c> alsaydı çağıranın kapsam
/// kontrolünü <i>hatırlaması</i> gerekirdi ve unutulduğu gün hiçbir şey
/// kırılmazdı — A grubunun paketinden üretilmiş bir rapor B grubuna okunurdu ve
/// ne hata ne sayaç ne belirti olurdu.
/// </para>
///
/// <para>
/// Paketi istemek o kontrolü <b>zaten yapılmış</b> kılıyor: bir
/// <see cref="EvidenceBundle"/> örneği elde etmenin yolu
/// <c>EvidenceBundleStore</c>'dan geçiyor ve uç onu
/// <c>Scope.IsReadableBy</c>'dan geçirmeden döndürmüyor. T41/T42/T44'te üç kez
/// verilen kararın aynısı: kapıyı çağrı alışkanlığı olmaktan çıkarıp imzaya
/// bağlamak.
/// </para>
/// </summary>
public sealed class RcaReportStore(IDbContextFactory<ControlPlaneDbContext> factory, TimeProvider time)
{
    /// <summary>
    /// Bugünkü kodun okuyabildiği en eski belge sürümü.
    ///
    /// <para>
    /// <c>CurrentSchemaVersion</c>'dan ayrı: biri "ne yazıyoruz", bu "ne
    /// okuyabiliyoruz". Tek sayıya bağlamak, sürümü artıran ilk kişinin bütün
    /// geçmiş raporları okunamaz yapmasına ve bunu fark etmemesine yol açardı.
    /// </para>
    /// </summary>
    public const int MinReadableSchemaVersion = 1;

    public async Task<Guid> SaveAsync(RcaReportDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entity = ToEntity(document, time.GetUtcNow());
        db.RcaReports.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    /// <summary>
    /// Bu paketin <b>son</b> raporu; hiç üretilmemişse <see langword="null"/>.
    ///
    /// <para>
    /// <b><see langword="null"/> "bulgu yok" DEĞİL</b> — <i>"model hiç
    /// koşmadı"</i>. Ayrım ekrana kadar taşınıyor ve orada bir bekçisi var:
    /// koşup her cümlesi atılmış bir rapor da boş bulgu listesi taşıyor, ve
    /// ikisi aynı kutuya düşerse F4'ün ölçmek istediği şey tam olarak kaybolur.
    /// </para>
    ///
    /// <para>
    /// Tekil değil, <b>en yeni</b>: aynı paket üzerinde farklı model/prompt
    /// koşturmak F4'ün karşılaştırma akışı. Ekranın sorduğu soru "son söz ne";
    /// hepsini isteyen <see cref="AllForAsync"/>'a bakıyor.
    /// </para>
    /// </summary>
    public async Task<StoredRcaReport?> LatestForAsync(
        EvidenceBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entity = await db.RcaReports
            .AsNoTracking()
            .Where(r => r.BundleId == bundle.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : Read(entity);
    }

    /// <summary>
    /// Bu paketin bütün raporları, yeniden eskiye. F4'ün <i>"aynı kanıt, iki
    /// model"</i> karşılaştırması bunu okuyor.
    /// </summary>
    public async Task<IReadOnlyList<StoredRcaReport>> AllForAsync(
        EvidenceBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entities = await db.RcaReports
            .AsNoTracking()
            .Where(r => r.BundleId == bundle.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);

        return [.. entities.Select(Read)];
    }

    /// <summary>
    /// Okunamayan bir sürüm <b>istisna fırlatıyor</b>, boş dönmüyor: "rapor
    /// yok" ile "rapor var ama okuyamıyoruz" farklı şeyler, ve ikincisini
    /// birincisi gibi göstermek T47'nin kalite ölçümünü sessizce eksik kümeye
    /// indirger — yani ölçüm kendi körlüğünü iyi haber diye raporlar.
    /// </summary>
    private static StoredRcaReport Read(RcaReportEntity entity)
    {
        if (entity.SchemaVersion < MinReadableSchemaVersion
            || entity.SchemaVersion > ReasoningSerializer.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"RCA raporu {entity.Id} sürüm {entity.SchemaVersion} ile yazılmış; " +
                $"bugünkü kod {MinReadableSchemaVersion}..{ReasoningSerializer.CurrentSchemaVersion} okuyor.");
        }

        return new StoredRcaReport(
            entity.Id,
            entity.CreatedAt,
            ReasoningSerializer.Deserialize(entity.Payload));
    }

    /// <summary>
    /// Belge ve üst veri <b>aynı kaynaktan</b>. Kopyanın bedeli budur ve tek
    /// yerde ödeniyor.
    ///
    /// <para>
    /// Zaman <b>parametre</b>, <c>DateTimeOffset.UtcNow</c> değil: bu depoda
    /// duvar saatine bağlı kararlar iki kez pahalıya patladı, ve bir kaydın
    /// zamanını sınamak için beklemek zorunda kalan bir test kararsızdır.
    /// </para>
    /// </summary>
    internal static RcaReportEntity ToEntity(RcaReportDocument document, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BundleId = document.BundleId,
        CreatedAt = now,
        SchemaVersion = ReasoningSerializer.CurrentSchemaVersion,
        ScenarioId = document.ScenarioId,
        ScenarioVersion = document.ScenarioVersion,
        ProducedSentenceCount = document.ProducedSentenceCount,
        DroppedSentenceCount = document.DroppedSentenceCount,
        FabricatedCitationSentenceCount = document.FabricatedCitationSentenceCount,
        Payload = ReasoningSerializer.Serialize(document),
    };
}

namespace Bizigo.ScenarioPlugin;

/// <summary>Derleme işareti — mimari testler bu türden derlemeyi buluyor.</summary>
public static class ScenarioMarker;

/// <summary>
/// Çekirdeğin <b>taşıdığı ama okumadığı</b> serbest belge (F4 plugin formatı §5.2).
///
/// <para>
/// YamlDotNet tipleri bilerek dışarı sızmıyor: <see cref="IScenarioEvidenceSchema"/>
/// uygulayan bir sağlayıcının YAML kütüphanesine bağımlı olması gerekmemeli —
/// doğrulaması kendi alanlarına bakıyor, dosya biçimine değil.
/// </para>
/// </summary>
public abstract record ScenarioValue
{
    private ScenarioValue()
    {
    }

    public sealed record Scalar(string Value) : ScenarioValue;

    public sealed record Sequence(IReadOnlyList<ScenarioValue> Items) : ScenarioValue;

    public sealed record Mapping(IReadOnlyDictionary<string, ScenarioValue> Fields) : ScenarioValue
    {
        public static Mapping Empty { get; } = new(new Dictionary<string, ScenarioValue>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Senaryonun kimliği. <c>version</c> semantik sürüm; <c>owner</c> zorunlu çünkü
/// bir senaryonun sahibi yoksa muafiyet gerekçesini kimin yazdığı da sorulamaz.
/// </summary>
public sealed record ScenarioMetadata
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Owner { get; init; }

    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Kanıt bloğu — <b>zarf</b> ile <b>içerik</b> ayrı duruyor (§6.1).
///
/// <para>
/// <see cref="Providers"/> zarf: adları çekirdek biliyor, yükleme anında
/// kayıtlı olup olmadıklarını sınıyor. <see cref="Content"/> içerik: çekirdek
/// onu <b>hiç yorumlamıyor</b>, sağlayıcıya olduğu gibi veriyor. Üç senaryo üç
/// farklı şekil istedi (<c>window</c>, <c>horizon</c>, <c>sample</c>); ortak bir
/// şekil uydurmak üçünü de bükmek olurdu.
/// </para>
/// </summary>
public sealed record ScenarioEvidenceBlock
{
    public required IReadOnlyList<string> Providers { get; init; }

    /// <summary><c>providers</c> dışındaki her şey. Çekirdek taşıyor, okumuyor.</summary>
    public ScenarioValue.Mapping Content { get; init; } = ScenarioValue.Mapping.Empty;
}

/// <summary>
/// Bir adımın çıktı sözleşmesi.
/// </summary>
public sealed record ScenarioOutput
{
    public required string Schema { get; init; }

    /// <summary>
    /// Motorun <b>zorladığı</b> kısıtlar. Ad kümesi bilerek <b>açık</b>:
    /// <c>pattern_must_compile</c> format kararı yazılırken doğdu, kaç tane
    /// olacağını kimse bilmiyor ve küme kapatılırsa her yeni senaryo çekirdeği
    /// değiştirir (§5.2).
    /// </summary>
    public IReadOnlyList<string> Constraints { get; init; } = [];

    /// <summary>
    /// Kısıtsızlığın <b>adı</b>. Boş liste ancak gerekçesiyle geçerli; gerekçesiz
    /// boş liste yüklenmiyor (§3.2 · §5.1(b)).
    /// </summary>
    public string? ConstraintsWaived { get; init; }

    /// <summary>
    /// Çıktı listesi tavanı. Çekirdek <b>yorumlamıyor</b>, taşıyor — zorlaması
    /// T44'ün. RCA §8.1 bu sayıyı <c>⚠ seçildi, ölçülmedi</c> diye işaretliyor ve
    /// işaret senaryo dosyasında duruyor.
    /// </summary>
    public int? MaxItems { get; init; }

    /// <summary>
    /// Bu adımda motorun zorladığı hiçbir kısıt yok — ve bunun <b>yazılı bir
    /// gerekçesi</b> var. Muaf adım sayısı ayrıca bir test sabitiyle tutuluyor;
    /// muafiyet eklemek iki ayrı bilinçli hareket.
    /// </summary>
    public bool IsWaived => Constraints.Count == 0;
}

/// <summary>
/// Tek iş yapan tek adım (K15). Sıra anlamlı: <c>input</c>'taki
/// <c>steps.&lt;id&gt;</c> atıfları yalnızca <b>daha önce tanımlanmış</b> adımlara
/// bakabiliyor.
/// </summary>
public sealed record ScenarioStep
{
    public required string Id { get; init; }

    public required string Task { get; init; }

    /// <summary><c>evidence.&lt;yol&gt;</c> ya da <c>steps.&lt;id&gt;</c>.</summary>
    public required IReadOnlyList<string> Input { get; init; }

    public required ScenarioOutput Output { get; init; }
}

/// <summary>
/// Konu — RCA'da yok, parser kalite senaryosunda var. Bugün açılıyor çünkü
/// sonradan açmanın bedeli yazılmış senaryoların göçü (§5.1(c)).
/// </summary>
public sealed record ScenarioSubject
{
    public required string Kind { get; init; }
}

/// <summary>
/// K16: aksiyon alan senaryo onaysız yayınlanmıyor.
/// </summary>
public sealed record ScenarioPublish
{
    public required bool RequiresReview { get; init; }

    /// <summary>Depo artifact'ına öneri yazan senaryolar için; RCA'da yok.</summary>
    public string? Target { get; init; }
}

/// <summary>
/// Yüklenmiş bir senaryo plugin'i — <b>zarfı doğrulanmış</b>, içeriği değil.
///
/// <para>
/// Bu tipin varlığı tek başına şunu söylüyor: apiVersion/kind tanınıyor, kimlik
/// ve sürüm biçimli, tetikleyiciler kapalı kümeden, adım grafiği ileri atıf
/// içermiyor, kanıt sağlayıcılarının hepsi kayıtlı, ve <b>her adımın ya kısıtı
/// ya gerekçeli muafiyeti var</b>.
/// </para>
///
/// <para>
/// Söylemediği şey: <see cref="ScenarioEvidenceBlock.Content"/>'in sağlayıcının
/// şemasına uyduğu. O koşum anının işi (<see cref="ScenarioEvidenceGate"/>) ve
/// ihlali adı konmuş bir hatayla duruyor — sessizce çalışan bir senaryo
/// doğmuyor.
/// </para>
/// </summary>
public sealed record ScenarioDefinition
{
    public required string ApiVersion { get; init; }

    public required ScenarioMetadata Metadata { get; init; }

    public required IReadOnlyList<string> Triggers { get; init; }

    public required ScenarioEvidenceBlock Evidence { get; init; }

    public required IReadOnlyList<ScenarioStep> Steps { get; init; }

    public required ScenarioPublish Publish { get; init; }

    public ScenarioSubject? Subject { get; init; }

    public string SourcePath { get; init; } = "<inline>";

    /// <summary>Gerekçeli muafiyet taşıyan adımlar — <c>&lt;senaryo&gt;/&lt;adım&gt;</c>.</summary>
    public IEnumerable<string> WaivedStepKeys =>
        Steps.Where(s => s.Output.IsWaived).Select(s => $"{Metadata.Id}/{s.Id}");
}

using Bizigo.Api;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// İnceleme sözleşmesinin <b>tel adları</b> (T37 ↔ T38).
///
/// <para>
/// Ekran bu dizgilere göre dallanıyor ve onları <b>elle</b> yazıyor
/// (<c>ui/src/lib/rca/report.ts</c>): TypeScript C# enum'unu okuyamıyor, yani
/// iki taraf arasında derleyicinin kovaladığı bir bağ <b>yok</b>. Dil sınırının
/// iki yanına birer çivi çakmak, bu bağı sınayabilmenin tek yolu — biri
/// kayarsa ikisinden biri kırmızı yanıyor.
/// </para>
///
/// <para>
/// Kaymanın bedeli sessiz: ekranın gönderdiği <c>partially</c> sunucuda
/// tanınmayan bir karar olurdu, kullanıcı düğmeye basar, istek 400 döner ve
/// altın küme o incelemeyi hiç görmez. Ya da tersi — sunucu yeni bir değer
/// ekler, ekran onu hiç gönderemez ve boyut <b>ölçülüyormuş gibi görünürken</b>
/// hiç örneklenmez.
/// </para>
///
/// <para>
/// <c>ToString()</c>'e bırakılmamasının sebebi ayrıca yazılı:
/// <c>NotPresent</c> tele <c>NotPresent</c> diye inerdi — camelCase
/// politikasının <c>idp_groups</c>'u <c>idpGroups</c> yaptığı kazanın aynısı.
/// </para>
/// </summary>
public sealed class RcaReviewWireTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

    private static string WireVerdict(ReviewVerdict verdict) =>
        RcaReviewResponse.Of(Entity(verdict, ContradictingEvidenceVerdict.Unknown)).Verdict;

    private static string WireContradicting(ContradictingEvidenceVerdict value) =>
        RcaReviewResponse.Of(Entity(ReviewVerdict.Correct, value)).ContradictingEvidence;

    private static GoldenReviewEntity Entity(
        ReviewVerdict verdict,
        ContradictingEvidenceVerdict contradicting) => new()
    {
        Id = Guid.CreateVersion7(Now),
        BundleId = Guid.CreateVersion7(Now),
        ReviewedAt = Now,
        OwnerGroup = "network/core",
        Verdict = verdict,
        ContradictingEvidence = contradicting,
        ReviewerSubject = "analyst",
        ActualRootCause = string.Empty,
        Note = string.Empty,
    };

    /// <summary>
    /// Dört karar, dört <b>ayrı</b> ve <b>sabit</b> dizgi. Ekrandaki ikizi
    /// <c>rca-screen.test.tsx > Dort_karar_degeri_de_gonderilebiliyor</c>.
    /// </summary>
    [Fact]
    public void Karar_tel_adlari_sabit()
    {
        Assert.Equal("correct", WireVerdict(ReviewVerdict.Correct));
        Assert.Equal("wrong", WireVerdict(ReviewVerdict.Wrong));
        Assert.Equal("incomplete", WireVerdict(ReviewVerdict.Incomplete));
        Assert.Equal("unknown", WireVerdict(ReviewVerdict.Unknown));
    }

    /// <summary>
    /// Çelişen kanıt kararının dört tel adı. <c>not_present</c> tek kelimeye
    /// düşerse ekranın seçeneği sunucuda tanınmaz.
    /// </summary>
    [Fact]
    public void Celisen_kanit_tel_adlari_sabit()
    {
        Assert.Equal("not_present", WireContradicting(ContradictingEvidenceVerdict.NotPresent));
        Assert.Equal("sound", WireContradicting(ContradictingEvidenceVerdict.Sound));
        Assert.Equal("trivial", WireContradicting(ContradictingEvidenceVerdict.Trivial));
        Assert.Equal("unknown", WireContradicting(ContradictingEvidenceVerdict.Unknown));
    }

    /// <summary>
    /// <b>Enum'a eklenen bir değer tel adını almadan geçemiyor.</b>
    ///
    /// <para>
    /// Yukarıdaki iki test bugünkü değerleri çiviliyor ama beşinci bir değer
    /// eklendiğinde ikisi de <b>yeşil</b> kalırdı — ve o değer ekranda hiç
    /// görünmeden, hiç gönderilmeden yaşardı. Sayıyı burada tutmak, eklemeyi
    /// bilinçli bir hareket yapıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Enum_degerleri_ekrandaki_listeyle_ayni_sayida()
    {
        Assert.Equal(4, Enum.GetValues<ReviewVerdict>().Length);
        Assert.Equal(4, Enum.GetValues<ContradictingEvidenceVerdict>().Length);
    }

    /// <summary>
    /// Her enum değeri <b>benzersiz</b> bir tel adı veriyor; ikisinin aynı
    /// dizgiye düşmesi altın kümede iki boyutu tek sayıya indirirdi.
    /// </summary>
    [Fact]
    public void Tel_adlari_benzersiz()
    {
        var verdicts = Enum.GetValues<ReviewVerdict>().Select(WireVerdict).ToArray();
        var contradicting = Enum.GetValues<ContradictingEvidenceVerdict>().Select(WireContradicting).ToArray();

        Assert.Equal(verdicts.Length, verdicts.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(contradicting.Length, contradicting.Distinct(StringComparer.Ordinal).Count());
    }
}

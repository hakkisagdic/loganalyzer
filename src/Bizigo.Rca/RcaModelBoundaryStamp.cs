using Bizigo.ControlPlane;
using Bizigo.Rca.Models;

namespace Bizigo.Rca;

/// <summary>
/// <b>Bir koşumun model sınırı damgası</b> — ve damganın <i>gerekçesiz var
/// olamaması</i> (T54).
///
/// <para>
/// Kalıp T41'in <c>RedactedPrompt</c>'undan ve M06'nın
/// <c>McpBoundaryDeclaration</c>'ından: yapıcı <see langword="private"/>,
/// dolayısıyla bir damga yalnızca aşağıdaki iki fabrikadan çıkabiliyor ve
/// <b>geçersiz bir damga hiç var olamıyor</b>. Muafiyetin gerekçesiz
/// kaydedilmesi bir çalışma anı kontrolüyle değil, tipin varoluş şartıyla
/// engelleniyor.
/// </para>
///
/// <para>
/// <b>Neden bir tip, neden iki parametre değil.</b>
/// <c>TryStartAsync(runId, boundary, reason)</c> imzası aynı bilgiyi taşırdı ve
/// <c>Overridden</c> + <c>null</c> bileşimini <b>derlenebilir</b> bırakırdı —
/// yani kaydın en önemli iddiası yine bir çağrı alışkanlığına bağlı olurdu. Bu
/// depoda "unutmanın bedeli" tekrar tekrar ölçüldü ve çözümü hep aynı:
/// unutmayı <b>derlenmez</b> kıl.
/// </para>
///
/// <para>
/// <b>Parametre zorunlu ve isteğe bağlı değil.</b> Varsayılan değer vermek
/// (<c>= null</c>) damgayı yeniden unutulabilir yapardı: model yolu bağlandığı
/// gün çağıran hiçbir şey değiştirmeden derlenir ve kayıt sessizce
/// <see cref="RcaModelBoundary.Unspecified"/> kalırdı. Bugün tek çağıran
/// <c>RcaScheduleWorker</c> ve <see cref="NotEngaged"/> geçiyor; model yolu
/// doğduğu gün <b>derleyici</b> o satırı yeniden okumaya zorluyor.
/// </para>
///
/// <para>
/// <b>Ölçümün kapsamı — yazılmazsa kapı varmış gibi okunur.</b>
/// <see cref="RcaModelBoundary.Overridden"/>'ın gerekçesiz var olamayacağı
/// <b>tip düzeyinde</b> ölçüldü ve koşum gerektirmiyor. Bir <b>üretim</b>
/// koşumunun <c>Overridden</c> ya da <see cref="RcaModelBoundary.Verified"/>
/// damgalandığı <b>ölçülmedi</b>: bugün modeli çağıran bir üretim yolu yok,
/// dolayısıyla üretimdeki tek değer <see cref="RcaModelBoundary.NotEngaged"/>.
/// İkisi farklı iddialar ve ikincisi bu ticket'ın kapsamı dışında
/// (<c>ModelBoundaryGate.VerifyAsync</c> kayıtlı, yapılandırması bağlı, ve
/// hiçbir üretim kodu çağırmıyor).
/// </para>
/// </summary>
public sealed class RcaModelBoundaryStamp
{
    private RcaModelBoundaryStamp(RcaModelBoundary boundary, string? reason)
    {
        Boundary = boundary;
        Reason = reason;
    }

    /// <summary>Koşum hakkında sınır konusunda söylenen şey.</summary>
    public RcaModelBoundary Boundary { get; }

    /// <summary>
    /// Muafiyetin gerekçesi; yalnızca
    /// <see cref="RcaModelBoundary.Overridden"/> hâlinde dolu, diğer üç hâlde
    /// <see langword="null"/>.
    ///
    /// <para>
    /// <b><see langword="null"/> ile boş dize aynı şey değil</b> ve bu tip
    /// ikincisini hiç üretmiyor: <c>null</c> <i>"muafiyet yok"</i>, boş dize
    /// <i>"muafiyet var, gerekçe yazılmamış"</i>.
    /// </para>
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// <b>Koşum modele hiç konuşmadı.</b> Sınır hakkında söylenecek bir şey yok
    /// ve yokluğu <i>yazılıyor</i> — <see cref="RcaModelBoundary.Unspecified"/>
    /// ile karıştırılmasın diye ayrı bir değer.
    /// </summary>
    public static RcaModelBoundaryStamp NotEngaged() =>
        new(RcaModelBoundary.NotEngaged, null);

    /// <summary>
    /// Damgayı <b>ucun kendisinden</b> kuruyor: muafiyetin varlığı da gerekçesi
    /// de <see cref="ModelEndpoint"/>'te zaten duruyor ve buradan yeniden
    /// karar verilmiyor.
    ///
    /// <para>
    /// Gerekçeyi çağırandan almamasının sebebi tek: alsaydı çağıran
    /// <c>Overridden</c> deyip başka bir metin — ya da hiç metin —
    /// geçirebilirdi, ve kayıt ucun gerçekten hangi gerekçeyle açıldığından
    /// <b>ayrışabilirdi</b>. Tek gerçek kaynak uç.
    /// </para>
    ///
    /// <para>
    /// Boş gerekçeli bir muafiyet <c>ModelBoundaryGate</c> tarafından zaten
    /// reddediliyor, ama bu fabrika ona <b>güvenmiyor</b>: kaydın doğruluğu
    /// başka bir tipin kapısına bırakılamaz, çünkü o kapı bir gün gevşerse
    /// ayrışma burada sessiz kalır.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Uç kendini muaf ilan ediyor ama gerekçesi boş.
    /// </exception>
    public static RcaModelBoundaryStamp From(ModelEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.BoundaryOverridden)
        {
            return new RcaModelBoundaryStamp(RcaModelBoundary.Verified, null);
        }

        if (string.IsNullOrWhiteSpace(endpoint.BoundaryOverrideReason))
        {
            throw new ArgumentException(
                "Model sınırı muafiyeti gerekçesiz kaydedilemez. Gerekçesiz bir muafiyet, "
                + "koşum kaydında \"muafiyet yok\" hâlinden ayırt edilemez hâle gelir ve "
                + "muafiyetin sessiz olması muafiyetin olmamasından tehlikelidir.",
                nameof(endpoint));
        }

        return new RcaModelBoundaryStamp(
            RcaModelBoundary.Overridden,
            endpoint.BoundaryOverrideReason);
    }
}

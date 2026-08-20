using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Api.Connectors;

/// <param name="Credential">
/// Çözülmüş kimlik bilgisi. Kaydı yoksa boş dizge — runner'lar
/// <see langword="null"/> kontrolü yapmak zorunda kalmasın.
/// </param>
/// <param name="Scope">
/// Connector'ın kapsamı: <b>yalnızca kendi grubuna</b> yazabiliyor. Runner bunu
/// <c>IScopedQuery</c>'ye olduğu gibi veriyor, kendi kapsamını uydurmuyor.
/// </param>
public sealed record ConnectorContext(
    ChangeConnectorEntity Connector,
    string Credential,
    AccessScope Scope);

/// <param name="Message">
/// Kullanıcıya gösterilecek metin. <b>Runner bunu temizlemekle yükümlü değil</b> —
/// <see cref="ChangeConnectorService"/> her hâlükârda redaksiyondan geçiriyor.
/// </param>
public sealed record ConnectorTestResult(bool Ok, string Message);

public sealed record ConnectorRunResult(bool Ok, int ChangesWritten, string Error);

/// <summary>
/// Bir connector tipini çalıştırabilen bileşen (T25).
///
/// <para>
/// <b>Bu arayüz T26'nın oturacağı yer.</b> Cihaz config toplayıcısı yalnızca
/// bunu uygulayacak: yapılandırma, zamanlama, kimlik bilgisi saklama, çalışma
/// geçmişi ve kapsam kapısı burada zaten çözülmüş durumda.
/// </para>
///
/// <para>
/// <b>Kayıtlı runner'ı olmayan tip etkinleştirilemiyor.</b> Alternatif —
/// zamanlayıcının her turda "bu tip için toplayıcı yok" diye hata yazması —
/// çalışma geçmişini gerçek arızalarla sahte arızaların karıştığı bir yığına
/// çevirirdi. Reddetmek, kullanıcıya doğru anı söylüyor: kaydetme anında.
/// </para>
/// </summary>
public interface IChangeConnectorRunner
{
    ChangeConnectorType ConnectorType { get; }

    /// <summary>
    /// "Erişebiliyor muyum" denemesi. Kaydetmeden önce çağrılabiliyor, çünkü
    /// yanlış bir kimlik bilgisiyle kaydedilen connector'ın arızası ancak ilk
    /// zamanlanmış koşuda — belki saatler sonra — görünürdü.
    /// </summary>
    Task<ConnectorTestResult> TestAsync(ConnectorContext context, CancellationToken cancellationToken);

    Task<ConnectorRunResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken);
}

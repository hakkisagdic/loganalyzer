using System.Security.Claims;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Kimliğin ÜRETİLEMEDİĞİ hâlin sebebi</b> — ve bu arayüzün tek işi o sebebi
/// araç hatasına taşımak (M13).
///
/// <para>
/// <c>McpCallerScope</c> kimlik yokluğunu tek bir cümleyle bildiriyordu
/// (<see cref="McpCallerScope.NoIdentityMessage"/>) ve HTTP tarafında bu doğru:
/// orada kimliğin yokluğunun tek bir sebebi var — istemci belirteç göndermedi.
/// </para>
///
/// <para>
/// <b>stdio'da iki farklı sebep var ve ikisi zıt iş gerektiriyor:</b> ortamda
/// belirteç <i>hiç yok</i> (operatör kurmadı) ya da belirteç vardı ve
/// <i>süresi doldu</i> (yenileme yok — M13'ün bilinçli sınırı). İkisi aynı
/// cümleye düşerse okuyan kişi yanlış yere bakar: birincisinde yapılacak şey
/// yapılandırma, ikincisinde yeni bir belirteç almak.
/// </para>
///
/// <para>
/// <b>Kod AYNI kalıyor</b> (<c>unauthenticated</c>) ve kalması gerekiyor:
/// istemcinin dallanacağı şey değişmedi, çağrı gerçekten kimliksiz. Değişen
/// yalnızca <b>insana okunan cümle</b>. Kodu ayırmak, kapalı hata kümesini bir
/// taşıma ayrıntısıyla büyütmek olurdu.
/// </para>
///
/// <para>
/// HTTP tarafında bu servis <b>kayıtlı değil</b> ve olmaması doğru:
/// <c>McpCallerScope</c> onu <c>GetService</c> ile arıyor, bulamazsa eski
/// cümlede kalıyor.
/// </para>
/// </summary>
public interface IMcpIdentityRefusal
{
    /// <summary>
    /// Kimlik neden üretilemiyor — ya da kimlik <b>varsa</b>
    /// <see langword="null"/>.
    /// </summary>
    string? Reason { get; }
}

/// <summary>
/// <b>Gelen her mesaja bir kimlik damgalayan taşıma sarmalayıcısı.</b>
///
/// <para>
/// Üretimde akışlanabilir HTTP taşıması bunu kendisi yapıyor:
/// <c>HttpContext.User</c> → <c>JsonRpcMessageContext.User</c>. stdio'da
/// taşıma katmanının böyle bir kaynağı <b>yok</b> — MCP yetkilendirme
/// spesifikasyonu stdio'yu kapsam dışında bırakıyor — dolayısıyla kimliği
/// <b>süreç</b> koyuyor: doğrulanmış bir belirteçten (M13).
/// </para>
///
/// <para>
/// SDK bu alanı bilerek açık bırakıyor; belgesi <i>"should only be set when
/// implementing a custom ITransport"</i> diyor ve burada yapılan tam olarak o.
/// </para>
///
/// <h3>Kimlik SABİT DEĞİL, her mesajda SORULUYOR</h3>
///
/// <para>
/// <paramref name="user"/> bir <c>ClaimsPrincipal</c> değil bir
/// <b>fonksiyon</b>, ve bu M13'ün taşıyıcı kararı: belirtecin <c>exp</c>'si
/// gerçek bir sınır ve bir kez damgalanan sabit bir kimlik onu <b>aşardı</b> —
/// yani stdio kimliği hiç sona ermezdi. Süresi dolmuş bir belirteçle
/// çalışmaya devam eden bir yüzey, K17'nin alanında sessizce yanlış olan bir
/// hâl.
/// </para>
///
/// <h3>Bu tip ÜÇÜNCÜ bir kopya değil — birleştirme</h3>
///
/// <para>
/// Aynı mekanizma <c>Bizigo.UnitTests</c> içinde <c>IdentityStampingTransport</c>
/// adıyla duruyordu (uyum kapısının kimlikli oturumu için). M13 üretimde de
/// ihtiyaç duyunca ikinci bir kopya yazmak yerine buraya taşındı ve test
/// tarafı bunu çağırıyor (§9). Aşağıdaki <b>bir kez sarmalama</b> kuralı o
/// kopyada ölçülmüş bir kusurdu ve birleşmeyle korunuyor.
/// </para>
/// </summary>
/// <param name="inner">Sarmalanan taşıma.</param>
/// <param name="user">
/// Her mesaj için kimlik. <see langword="null"/> döndürmesi meşru: kimliksiz
/// çağrıyı reddetmek <c>McpCallerScope</c>'un işi, taşımanın değil.
/// </param>
public sealed class McpIdentityTransport(ITransport inner, Func<ClaimsPrincipal?> user) : ITransport
{
    private Channel<JsonRpcMessage>? stamped;

    /// <inheritdoc/>
    public string? SessionId => inner.SessionId;

    /// <inheritdoc/>
    public ChannelReader<JsonRpcMessage> MessageReader => Stamped().Reader;

    /// <inheritdoc/>
    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
        inner.SendMessageAsync(message, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Okuyucuyu <b>bir kez</b> sarmalıyor.
    ///
    /// <para>
    /// Her erişimde yeni bir kanal kurmak, mesajların iki okuyucu arasında
    /// bölünmesi demek olurdu ve arıza <i>"bazı çağrılar cevapsız"</i> diye
    /// görünürdü — sebebi hiçbir yerde durmayan bir belirti.
    /// </para>
    /// </summary>
    private Channel<JsonRpcMessage> Stamped()
    {
        if (stamped is not null)
        {
            return stamped;
        }

        stamped = Channel.CreateUnbounded<JsonRpcMessage>();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in inner.MessageReader.ReadAllAsync().ConfigureAwait(false))
                {
                    // KİMLİK MESAJ BAŞINA SORULUYOR — gerekçe sınıf belgesinde.
                    if (user() is { } principal)
                    {
                        message.Context ??= new JsonRpcMessageContext();
                        message.Context.User = principal;
                    }

                    await stamped.Writer.WriteAsync(message).ConfigureAwait(false);
                }

                stamped.Writer.TryComplete();
            }
            catch (Exception error)
            {
                // Sessizce yutmuyoruz: yutulan bir hata "istemci cevap beklerken
                // asılı kaldı" diye görünür ve sebebi hiçbir yerde durmaz.
                stamped.Writer.TryComplete(error);
            }
        });

        return stamped;
    }
}

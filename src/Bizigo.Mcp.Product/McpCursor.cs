using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using Bizigo.Contracts;

namespace Bizigo.Mcp.Product;

/// <summary>
/// <b>MCP yüzeyinin sayfalama sözleşmesi: tek, opak, aynı adlı bir dize.</b>
///
/// <para>
/// <b>Bu tip §7'nin ilk örneğine karşı yazıldı.</b> F1'de yanıttaki imlecin adı
/// istekten farklıydı; ekran aldığı imleci geri gönderemiyordu ve <i>yarım
/// imleç sessizce ilk sayfayı tekrarlıyordu</i>. Hata yok, sayaç yok, belirti
/// yok. REST tarafında bu düzeltildi — <c>EventSearchRequest</c> ve
/// <c>EventCursorResponse</c> aynı iki adı taşıyor ve yarım imleç <c>400</c>
/// alıyor.
/// </para>
///
/// <para>
/// <b>MCP'de aynı kusurun bedeli daha büyük.</b> Bir ekran aynı sayfayı iki kez
/// gösterdiğinde kullanıcı fark eder. Bir model aynı sayfayı <b>sonsuz kez</b>
/// alır ve fark etmez: her turda yeni bir şey öğrendiğini sanır, bağlamı dolar,
/// ve verdiği cevap "veri bu kadar" olur. Bu yüzden sözleşme MCP tarafında
/// REST'ten daha <b>dar</b> çivilendi:
/// </para>
/// <list type="number">
/// <item>
/// <b>Tek alan.</b> İki alanlı bir imleç, yarısını gönderme ihtimalini
/// <i>ifade edilebilir</i> kılar. Burada yarım imleç diye bir hâl <b>yok</b>.
/// </item>
/// <item>
/// <b>Aynı ad.</b> Girdi şemasında <c>cursor</c>, çıktı şemasında da
/// <c>cursor</c>. Modelin yapması gereken şey <i>"aldığın değeri aynı adla geri
/// ver"</i>; alan adlarını eşlemek değil. Adların gerçekten aynı olduğu
/// <c>McpCursorContractTests</c> tarafından <b>şemalardan okunarak</b>
/// ölçülüyor — yorumdan değil.
/// </item>
/// <item>
/// <b>Opak.</b> Model imleci kuramıyor, yalnızca taşıyabiliyor. Uydurulmuş bir
/// imleç <c>invalid_argument</c> alıyor — <b>ilk sayfa değil</b>. "Bozuk imleci
/// yok say" davranışı, düzeltmeye çalıştığımız sessiz tekrarın ta kendisi olurdu.
/// </item>
/// </list>
///
/// <para>
/// <b>Neden ikinci bir gösterim değil.</b> Kodlanan şey <see cref="EventCursor"/>
/// — depodaki tek keyset imleci tipi. Burada yeni bir imleç <b>kavramı</b>
/// doğmuyor, var olanın tel üzerindeki <b>gösterimi</b> yazılıyor; REST'in iki
/// alanlı gösterimi de aynı tipin gösterimi. §6'nın "iki gösterim doğdu"
/// tuzağına düşmemesinin sebebi bu: ortada tek bir gerçek kaynak var ve
/// dönüşüm iki yönde de burada.
/// </para>
/// </summary>
internal static class McpCursor
{
    /// <summary>
    /// Girdi ve çıktı şemasında <b>aynı</b> alan adı. Sabit olarak duruyor ki
    /// iki şema onu ayrı ayrı yazmasın — ayrı yazılan iki ad ayrışabilir.
    /// </summary>
    internal const string FieldName = "cursor";

    /// <summary>
    /// Gösterim sürümü. Sürümsüz bir opak dize, biçim değiştiğinde <b>eski
    /// imleci sessizce yanlış çözerdi</b>: 25 baytın anlamı kaydığında ortaya
    /// hata değil <i>yanlış bir zaman damgası</i> çıkar — yani model başka bir
    /// sayfadan devam eder ve arada kalan satırlar kaybolur.
    /// </summary>
    private const byte Version = 1;

    /// <summary>1 sürüm baytı + 8 bayt tick + 16 bayt GUID.</summary>
    private const int PayloadLength = 1 + sizeof(long) + 16;

    /// <summary>
    /// İmleci opak dizeye çevirir. <see langword="null"/> girdi
    /// <see langword="null"/> döner — "sayfa bitti" hâli.
    /// </summary>
    internal static string? Encode(EventCursor? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[PayloadLength];

        bytes[0] = Version;

        // UTC tick: an KORUNUYOR, ofset korunmuyor. Keyset karşılaştırması ana
        // bakıyor, ofsete bakmıyor; ofseti de taşımak imleci uzatır ve
        // karşılaştırmaya hiçbir şey katmaz.
        BinaryPrimitives.WriteInt64LittleEndian(bytes[1..], cursor.Timestamp.UtcTicks);

        cursor.EventId.TryWriteBytes(bytes[(1 + sizeof(long))..]);

        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Opak dizeyi imlece çevirir.
    ///
    /// <para>
    /// <b>Tolerans yok.</b> Çözülemeyen imleç <see langword="false"/> döner ve
    /// çağıran <c>invalid_argument</c> üretir. "Bozuksa baştan başla" davranışı
    /// bu dosyanın var olma sebebini geri getirirdi.
    /// </para>
    /// </summary>
    internal static bool TryDecode(string? encoded, out EventCursor cursor, out string reason)
    {
        cursor = null!;

        if (string.IsNullOrWhiteSpace(encoded))
        {
            reason = "imleç boş";
            return false;
        }

        Span<byte> bytes = stackalloc byte[PayloadLength];

        if (Base64Url.DecodeFromChars(encoded, bytes, out _, out var written)
            is not OperationStatus.Done)
        {
            reason = "imleç base64url değil";
            return false;
        }

        if (written != PayloadLength)
        {
            // Kısa VE uzun ikisi de reddediliyor. Kısa olan kesilmiş bir imleç
            // (F1'in "yarım imleç"inin bu gösterimdeki karşılığı), uzun olan
            // başka bir biçim.
            reason = "imleç uzunluğu beklenenden farklı";
            return false;
        }

        if (bytes[0] != Version)
        {
            reason = "imleç sürümü tanınmıyor";
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[1..]);

        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            reason = "imleçteki zaman damgası geçersiz";
            return false;
        }

        cursor = new EventCursor(
            new DateTimeOffset(ticks, TimeSpan.Zero),
            new Guid(bytes[(1 + sizeof(long))..]));

        reason = string.Empty;
        return true;
    }
}

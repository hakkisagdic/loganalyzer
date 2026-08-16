using System.Text;

namespace Bizigo.Ingest.Text;

/// <param name="Name">Seçilen kodlamanın adı; <c>encoding_detected</c> kolonuna gider.</param>
/// <param name="Body">UTF-8 NFC normalize edilmiş metin.</param>
/// <param name="WasDeclaredHonored">Gönderenin iddiası tutmadıysa <see langword="false"/>.</param>
public sealed record DecodedBody(string Name, string Body, bool WasDeclaredHonored);

/// <summary>
/// Ham baytları metne çevirir (K4, F1 §2.4).
///
/// <para>
/// Sıra: BOM → envanterdeki/iddia edilen kodlama → UTF-8 doğrulaması → kaynağın
/// yedek kod sayfası → <c>latin1</c>. Son adım <b>kayıpsızdır</b>: her bayt dizisi
/// geçerli bir latin1 dizisidir, yani metne çevirme asla başarısız olmaz. Yanlış
/// tahminin bedeli kalıcı değil — orijinal baytlar ham arşivde duruyor ve replay
/// düzeltebiliyor (K12).
/// </para>
/// </summary>
public sealed class EncodingDetector
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static EncodingDetector() => RegisterCodePages();

    /// <summary>
    /// <b>Zorunlu.</b> Çağrılmazsa <c>windows-1254</c> / <c>iso-8859-9</c> çalışma
    /// anında patlar — .NET Core'da legacy kod sayfaları varsayılan olarak yüklü
    /// değil. Statik kurucu da çağırıyor; barındırıcı erken çağırabilsin diye açık.
    /// </summary>
    public static void RegisterCodePages() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public DecodedBody Decode(
        ReadOnlySpan<byte> bytes,
        string? declared = null,
        string? sourceFallback = null)
    {
        var body = StripBom(bytes, out var bomEncoding);

        // BOM tartışmasızdır: iddiadan da envanterden de güçlü.
        if (bomEncoding is not null)
        {
            return new DecodedBody(bomEncoding.WebName, Normalize(bomEncoding.GetString(body)), true);
        }

        if (TryResolve(declared, out var declaredEncoding)
            && TryDecodeStrict(declaredEncoding, body, out var declaredText))
        {
            return new DecodedBody(declaredEncoding.WebName, Normalize(declaredText), true);
        }

        // İddia edilmiş ama tutmamışsa bunu kaydediyoruz: envanterdeki yanlış
        // `encoding` alanı sessiz kalırsa yıllarca yanlış çözülür.
        var declaredHonored = string.IsNullOrWhiteSpace(declared);

        if (TryDecodeStrict(StrictUtf8, body, out var utf8Text))
        {
            return new DecodedBody("utf-8", Normalize(utf8Text), declaredHonored);
        }

        if (TryResolve(sourceFallback, out var fallbackEncoding)
            && TryDecodeStrict(fallbackEncoding, body, out var fallbackText))
        {
            return new DecodedBody(fallbackEncoding.WebName, Normalize(fallbackText), declaredHonored);
        }

        var latin1 = Encoding.Latin1;
        return new DecodedBody(latin1.WebName, Normalize(latin1.GetString(body)), declaredHonored);
    }

    /// <summary>
    /// NFC. Normalize edilmezse aynı Türkçe kelimenin iki farklı bayt dizilimi
    /// (<c>ğ</c> tek kod noktası vs. <c>g</c> + birleştirici) aramada eşleşmez.
    /// </summary>
    private static string Normalize(string text) =>
        text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

    private static bool TryResolve(string? name, out Encoding encoding)
    {
        encoding = StrictUtf8;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            // Kültür duyarlı ToLower() KULLANILMAZ (tr-TR: I → ı). Kodlama adı ordinal eşleşir.
            encoding = Encoding.GetEncoding(
                name.Trim(),
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return true;
        }
        catch (ArgumentException)
        {
            // Bilinmeyen kodlama adı sıradaki adaya bırakılır; ingest durmaz.
            return false;
        }
    }

    private static bool TryDecodeStrict(Encoding encoding, ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes, out Encoding? encoding)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = Encoding.UTF8;
            return bytes[3..];
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            return bytes[2..];
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            return bytes[2..];
        }

        encoding = null;
        return bytes;
    }
}

using System.IO.Compression;
using System.Text;
using Bizigo.Api;
using Microsoft.AspNetCore.Http;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>/v1/logs</c> gövde okuma sözleşmesi.
///
/// <para>
/// <b>gzip olağan yol.</b> OTLP/HTTP dışa aktarıcısı varsayılan olarak
/// sıkıştırıyor, yani üretimde gelen her yük buradan geçiyor. Açılmadığı sürece
/// gövde protobuf sanılıyor ve hata <c>invalid wire type</c> diye çıkıyor —
/// sıkıştırmadan hiç bahsetmeyen bir mesaj. F1'in uçtan uca ilk denemesinde veri
/// tam olarak böyle kayboldu ve sebebi ancak collector log'una bakınca anlaşıldı.
/// </para>
/// </summary>
public sealed class OtlpBodyReadTests
{
    private const long Limit = 1024 * 1024;

    private static HttpRequest Request(byte[] body, string? contentEncoding = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        if (contentEncoding is not null)
        {
            context.Request.Headers.ContentEncoding = contentEncoding;
        }

        return context.Request;
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task Sikistirilmamis_govde_oldugu_gibi_okunuyor()
    {
        var payload = Encoding.UTF8.GetBytes("ham gövde");

        var result = await LogsEndpoint.ReadBodyAsync(Request(payload), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal(payload, result.Bytes.ToArray());
    }

    [Fact]
    public async Task Gzip_govde_aciliyor()
    {
        // Türkçe gövde bilinçli: açma yolu bayt bayt çalışmazsa çok baytlı
        // karakterlerde bozulma en erken burada görünür.
        var payload = Encoding.UTF8.GetBytes("kullanıcı oturum açma başarısız — ĞÜŞÇÖİ");

        var result = await LogsEndpoint.ReadBodyAsync(
            Request(Gzip(payload), "gzip"), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal(payload, result.Bytes.ToArray());
    }

    [Fact]
    public async Task Identity_gzip_ile_karistirilmiyor()
    {
        var payload = Encoding.UTF8.GetBytes("ham gövde");

        var result = await LogsEndpoint.ReadBodyAsync(
            Request(payload, "identity"), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal(payload, result.Bytes.ToArray());
    }

    [Fact]
    public async Task Bilinmeyen_kodlama_reddediliyor()
    {
        var result = await LogsEndpoint.ReadBodyAsync(
            Request([1, 2, 3], "br"), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.UnsupportedEncoding, result.Status);
        Assert.Equal("br", result.Encoding);
    }

    /// <summary>
    /// Sınır <b>açılmış</b> boyuta uygulanıyor. <c>Content-Length</c> sıkıştırılmış
    /// boyutu söylüyor, dolayısıyla tek başına koruma değil: burada 200 baytlık
    /// bir istek 1 MB'lık sınırı aşan bir gövdeye açılıyor.
    /// </summary>
    [Fact]
    public async Task Zip_bomb_acilmis_boyuttan_yakalaniyor()
    {
        var payload = new byte[4 * 1024 * 1024];   // sıfırlar: çok iyi sıkışır
        var compressed = Gzip(payload);

        Assert.True(compressed.Length < 64 * 1024, "Test kurgusu: sıkıştırılmış gövde küçük olmalı.");

        var result = await LogsEndpoint.ReadBodyAsync(
            Request(compressed, "gzip"), Limit, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task Sikistirilmamis_buyuk_govde_de_reddediliyor()
    {
        var result = await LogsEndpoint.ReadBodyAsync(
            Request(new byte[2048]), limit: 1024, TestContext.Current.CancellationToken);

        Assert.Equal(LogsEndpoint.BodyStatus.TooLarge, result.Status);
    }
}

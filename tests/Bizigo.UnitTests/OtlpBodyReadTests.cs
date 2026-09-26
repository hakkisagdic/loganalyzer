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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Truncated_gzip_footer_is_not_success(int missing)
    {
        var compressed = Gzip(Encoding.UTF8.GetBytes("complete payload that must not be accepted without its footer"));
        var result = await LogsEndpoint.ReadBodyAsync(Request(compressed[..^missing], "gzip"), Limit,
            TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Concatenated_gzip_members_are_complete_and_crc_checked()
    {
        var first = Gzip("first"u8.ToArray());
        var second = Gzip("second"u8.ToArray());
        var result = await LogsEndpoint.ReadBodyAsync(Request([.. first, .. second], "gzip"), Limit,
            TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal("firstsecond", Encoding.UTF8.GetString(result.Bytes.Span));
        second[^8] ^= 1;
        result = await LogsEndpoint.ReadBodyAsync(Request([.. first, .. second], "gzip"), Limit,
            TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(65L)]
    public async Task Stream_limit_does_not_trust_content_length(long? declared)
    {
        var request = Request(new byte[65]);
        request.ContentLength = declared;
        if (declared is null) request.Headers.TransferEncoding = "chunked";
        var result = await LogsEndpoint.ReadBodyAsync(request, 64, TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.TooLarge, result.Status);
        result = await LogsEndpoint.ReadBodyAsync(Request(new byte[64]), 64, TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal(64, result.Bytes.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(1048679L)]
    public async Task Stored_gzip_overhead_does_not_reduce_expanded_limit(long? declared)
    {
        var payload = new byte[(int)Limit];
        new Random(42).NextBytes(payload);
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.NoCompression, true)) zip.Write(payload);
        var wire = output.ToArray();
        Assert.True(wire.Length > Limit);
        var request = Request(wire, "gzip");
        request.ContentLength = declared;
        if (declared is null) request.Headers.TransferEncoding = "chunked";
        var result = await LogsEndpoint.ReadBodyAsync(request, Limit, TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Ok, result.Status);
        Assert.Equal(payload, result.Bytes.ToArray());
    }

    [Fact]
    public async Task Excessive_gzip_metadata_is_bounded_separately_from_expanded_bytes()
    {
        // An untouched BCL GZipStream may emit no member at all. This is an
        // actual empty member with a deflate block, CRC and ISIZE trailer.
        var empty = Convert.FromHexString("1f8b080000000000000303000000000000000000");
        var valid = await LogsEndpoint.ReadBodyAsync(Request(empty, "gzip"), 64, TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.Ok, valid.Status);
        Assert.Empty(valid.Bytes.ToArray());
        var wire = Enumerable.Repeat(empty, 10000).SelectMany(x => x).ToArray();
        Assert.True(wire.Length > 65536 + 64);
        var request = Request(wire, "gzip");
        request.ContentLength = null;
        var result = await LogsEndpoint.ReadBodyAsync(request, 64, TestContext.Current.CancellationToken);
        Assert.Equal(LogsEndpoint.BodyStatus.TooLarge, result.Status);
    }
}

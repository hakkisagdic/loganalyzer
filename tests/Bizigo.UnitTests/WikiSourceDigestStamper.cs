namespace Bizigo.UnitTests;

/// <summary>
/// <b>Damgalayıcı</b> — vault sayfalarının <c>source_digest</c> alanını
/// kaynakların bugünkü hâline göre yazar.
///
/// <para>
/// Bekçi değil, <b>araç</b>. <see cref="WikiSourceDigestTests"/> kırmızı
/// yandığında insanın yapacağı iki adımın ikincisi: <i>önce sayfayı gözden
/// geçir, sonra yeniden damgala.</i>
/// </para>
///
/// <para>
/// <b>Neden ayrı sınıf ve neden çevre değişkeniyle kapalı:</b> damgayı yazan ile
/// doğrulayan aynı koşumda buluşursa doğrulama kendi çıktısını onaylar ve
/// hiçbir şey kanıtlamaz. Bekçi de bunu ayrıca reddediyor —
/// <c>BIZIGO_WIKI_STAMP=1</c> ile koşulduğunda kırmızı yanıyor. İki taraflı
/// kapatıldı çünkü tek taraflı hâli, birinin bir gün "hepsini birden koşturayım"
/// demesiyle sessizce açılırdı.
/// </para>
///
/// <para>
/// <b>Kullanımı:</b>
/// <code>
/// BIZIGO_WIKI_STAMP=1 dotnet test tests/Bizigo.UnitTests \
///   --filter FullyQualifiedName~WikiSourceDigestStamper
/// </code>
/// Değişken yokken <c>Assert.Skip</c> ile <b>görünür</b> biçimde atlıyor:
/// atlandığı koşum çıktısında yazılı, sessiz değil. Yeni sayfa ekleyen
/// birleştirmelerden sonra bunu koşturmak birleştirme protokolünün parçası —
/// üretilen bir alan elle birleştirilmez, kaynaktan yeniden üretilir (§5).
/// </para>
/// </summary>
public sealed class WikiSourceDigestStamper(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Vault_sayfalarini_damgala()
    {
        Assert.SkipUnless(
            WikiSourceDigest.StampRequested,
            $"{WikiSourceDigest.StampEnvironmentVariable}=1 gerekiyor — bu bir araç, bekçi değil.");

        var pages = WikiSourceDigest.Pages()
            .Where(page => page.HasSourcesKey)
            .ToArray();

        Assert.True(
            pages.Length > 0,
            $"{WikiSourceDigest.VaultPrefix}/ altında `sources` bildiren sayfa yok; damgalanacak bir şey yok.");

        var written = new List<string>();

        foreach (var page in pages)
        {
            if (WikiSourceDigest.Stamp(page))
            {
                written.Add(page.RepoPath);
            }
        }

        // Çıktı koşum günlüğünde dursun: hangi sayfaların damgası değişti,
        // gözden geçirilen sayfalarla aynı küme mi.
        _output.WriteLine(
            written.Count == 0
                ? $"{pages.Length} sayfa denetlendi, hepsinin damgası zaten güncel."
                : $"{pages.Length} sayfadan {written.Count} tanesi yeniden damgalandı:\n  "
                  + string.Join("\n  ", written));
    }
}

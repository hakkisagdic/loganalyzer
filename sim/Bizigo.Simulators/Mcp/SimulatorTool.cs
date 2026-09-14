using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp;

/// <summary>
/// <c>bizigo-sim</c> yüzeyindeki <b>her</b> aracın tabanı.
///
/// <para>
/// Tek işi yüzeyi bir kez beyan etmek. Yedi araçta tek tek yazılsaydı, birinin
/// onu yazmayı unutması <c>Unspecified</c> demek olurdu — ve o araç
/// <see cref="BizigoMcpTool.ProtocolTool"/> okunurken patlardı. Patlaması iyi
/// (M01'in kararı), ama hiç olmaması daha iyi.
/// </para>
///
/// <para>
/// <b>KİMLİKSİZ MUTASYON — M08'in bıraktığı sorunun cevabı, ve bilerek burada
/// yazılı.</b>
/// </para>
///
/// <para>
/// <see cref="BizigoMcpTool.RequiresCallerIdentity"/> ürün yüzeyinde
/// varsayılan <see langword="true"/>, bu yüzeyde <see langword="false"/> — yani
/// <c>sim.scenario.set</c> ve <c>sim.device.silence</c> <b>durum değiştiriyor</b>
/// ve stdio'da <b>kimliksiz</b> koşuyorlar. Bu bir eksik değil; sebebi şu ve
/// üçü birlikte geçerli olduğu sürece doğru:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Değişen şey ürün verisi değil.</b> Bu araçların dokunduğu tek kalıcı şey
/// <c>artifacts/bizigo-sim/state.json</c> — hangi <b>sahte</b> cihazın hangi
/// senaryoda olduğu. K17'nin kapsam tablosu, log arşivi, kontrol düzlemi:
/// hiçbirine yol yok. En kötü hâl <i>"yanlış bir simülatör durumu"</i>, ve
/// M03'ün ürün kolundan ayrılmasının gerekçesi zaten tam olarak bu.
/// </item>
/// <item>
/// <b>Yetkiyi kimlik değil YERLEŞİM veriyor.</b> Bu yüzeyin HTTP'si
/// <b>yok</b> (aşağıdaki karar); tek yolu <c>bizigo mcp serve --surface
/// bizigo-sim</c>, yani makinede o süreci başlatabilen biri. O kişi zaten
/// <c>catalog/simulators/*.yaml</c>'ı elle düzenleyebilir ve
/// <c>dotnet run --project sim/Bizigo.Simulators</c> ile aynı satırları
/// basabilir. Kimlik istemek, açık duran bir kapının yanına kilitli bir kapı
/// koymak olurdu — güvenlik değil tören.
/// </item>
/// <item>
/// <b>Kimlik istemek bugün aracı ÇALIŞMAZ yapardı.</b> Ölçüldü ve mekanizması
/// yazılı: <c>McpCallerScope.Resolve</c> kimliği <c>RequestContext.User</c>'dan
/// alıyor, stdio taşımasında o <see langword="null"/>, ve
/// <c>RequiresCallerIdentity</c> <see langword="true"/> olan bir araç
/// <b>hiç koşmadan</b> <c>unauthenticated</c> dönüyor. Yani "güvenli tarafa
/// düşelim" demek, yedi aracın tamamını tek kullanıcısı olan yüzeyde
/// kullanılamaz kılmak olurdu.
/// </item>
/// </list>
///
/// <para>
/// <b>Bu cevabın nerede biteceği de yazılı</b> — çünkü asıl risk cevabın değil
/// <i>dayanağının</i> sessizce değişmesi. Yukarıdaki üç maddeden herhangi biri
/// düşerse cevap düşüyor:
/// </para>
///
/// <list type="bullet">
/// <item>
/// Bir <c>sim.*</c> aracı ürün verisine uzanırsa (1 düşer). Kapsam yine de
/// taşınıyor: <see cref="McpToolInvocation.Scope"/> bu yüzeyde
/// <c>AccessScope.Denied</c>, yani o gün araç <b>hiçbir satır</b> görür —
/// M08'in "varsayılan kapalı" kararı bu ihtimali zaten karşılıyor.
/// </item>
/// <item>
/// <c>bizigo-sim</c>'e bir HTTP yüzeyi açılırsa (2 düşer): o an araca ulaşmak
/// için makinede bir süreç başlatmak gerekmiyor, ağdan erişilebiliyor.
/// </item>
/// </list>
///
/// <para>
/// <b>HTTP KARARI — M01 bunu M03'e bıraktı: açmıyorum.</b> Gerekçe iki katmanlı.
/// Birincisi M01'in kendi cümlesinin devamı: <c>bizigo-sim</c>'in HTTP'si ürün
/// API'sinde olmamalı, çünkü ürünün kendi süreci simülatör filosunu
/// değiştirebilir hâle gelirdi. İkincisi bu ticket'ın kendi ölçümü: durum
/// katmanı <b>dosyada</b> yaşıyor ve bunun tek sebebi stdio'nun her bağlantıya
/// ayrı süreç vermesiydi. Bir HTTP sunucusu açmak, o dosyayı gereksiz kılan
/// uzun ömürlü bir prosesi geri getirir — yani §3'ün kaçak proses dersini ve
/// yukarıdaki 2. maddeyi <b>aynı anda</b> bozar. Açılması gerektiği gün
/// değişmesi gereken şey bu yorum değil, bu paragrafın 2. maddesi.
/// </para>
/// </summary>
public abstract class SimulatorTool : BizigoMcpTool
{
    /// <inheritdoc/>
    public sealed override McpSurface Surface => McpSurface.Simulator;
}
